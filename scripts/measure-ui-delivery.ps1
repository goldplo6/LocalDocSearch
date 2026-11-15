param(
 [Parameter(Mandatory=$true)][string]$ExePath,
 [Parameter(Mandatory=$true)][string]$CorpusDirectory,
 [Parameter(Mandatory=$true)][string]$Label,
 [Parameter(Mandatory=$true)][int]$ExpectedFiles,
 [Parameter(Mandatory=$true)][int]$ExpectedOccurrences,
 [ValidateRange(1,5)][int]$Repeat=3
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe=(Resolve-Path -LiteralPath $ExePath).Path
$corpus=(Resolve-Path -LiteralPath $CorpusDirectory).Path
foreach($path in @($exe,$corpus)){if(-not $path.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Measurement inputs must belong to the workspace.'}}
if($Label -notmatch '^[a-zA-Z0-9-]+$'){throw 'Label must contain only letters, digits and hyphens.'}
$runtime='C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\6.0.11'
Add-Type -Path (Join-Path $runtime 'UIAutomationTypes.dll')
Add-Type -Path (Join-Path $runtime 'UIAutomationClient.dll')
$out=Join-Path $workspace ('artifacts/ui-delivery-'+$Label+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out|Out-Null
$rows=[Collections.Generic.List[object]]::new();$script:owned=$null;$script:window=$null
function Stamp-Corpus {
 $items=@(Get-ChildItem -LiteralPath $corpus -File -Recurse|Sort-Object FullName|ForEach-Object {
  if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Corpus reparse point.'}
  [ordered]@{path=[IO.Path]::GetRelativePath($corpus,$_.FullName);length=$_.Length;mtime=$_.LastWriteTimeUtc.Ticks;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
 })
 $text=ConvertTo-Json -InputObject $items -Depth 5 -Compress
 return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
}
function Find-Property($property,$value){
 $condition=[Windows.Automation.PropertyCondition]::new($property,$value)
 $found=$script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if($null -eq $found){
  $pidCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:owned.Id)
  foreach($root in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$pidCondition)){
   $found=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition);if($null -ne $found){break}
  }
 }
 if($null -eq $found -or $found.Current.ProcessId -ne $script:owned.Id){throw ('Missing owned control: '+$value)}
 return $found
}
function By-Name([string]$name){Find-Property ([Windows.Automation.AutomationElement]::NameProperty) $name}
function By-Id([string]$id){Find-Property ([Windows.Automation.AutomationElement]::AutomationIdProperty) $id}
function Invoke-Name([string]$name){(By-Name $name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Set-Value([string]$name,[string]$value){(By-Name $name).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)}
$report=[ordered]@{passed=$false;label=$Label;exe=$exe;exeSha256=(Get-FileHash -LiteralPath $exe).Hash;desktopSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'DocSearch.dll')).Hash;coreSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'LocalDocSearch.Core.dll')).Hash;corpus=$corpus;sourceBefore=$null;sourceAfter=$null;expectedFiles=$ExpectedFiles;expectedOccurrences=$ExpectedOccurrences;rounds=@();ownedProcessClosed=$false;clipboardTouched=$false;globalInput=$false;error=$null;semantics='Independent owned application and settings per round; OS cache not cleared; UIA overhead included externally; internal duration is app search-to-delivery timer, not parser-only; GC allocation is not measured by UIA; working-set peak is process lifetime.'}
try{
 $report.sourceBefore=Stamp-Corpus
 for($round=1;$round -le $Repeat;$round++){
  $settings=Join-Path $out "settings-$round.json"
  $script:owned=Start-Process -FilePath $exe -ArgumentList @('--settings',$settings) -PassThru -WorkingDirectory (Split-Path $exe) -RedirectStandardOutput (Join-Path $out "stdout-$round.log") -RedirectStandardError (Join-Path $out "stderr-$round.log")
  $deadline=[datetime]::UtcNow.AddSeconds(30)
  do{$script:owned.Refresh();if($script:owned.HasExited){throw 'Owned app exited before window.'};if($script:owned.MainWindowHandle -ne 0){break};Start-Sleep -Milliseconds 50}while([datetime]::UtcNow -lt $deadline)
  if($script:owned.MainWindowHandle -eq 0){throw 'Owned window missing.'}
  $script:window=[Windows.Automation.AutomationElement]::FromHandle($script:owned.MainWindowHandle)
  foreach($term in @('西瓜','苹果')){Set-Value '待添加关键词' $term;Invoke-Name '添加'}
  Set-Value '搜索根目录' $corpus
  $start=By-Name '开始搜索';$progress=By-Id 'SearchProgressText';$status=By-Id 'SearchStatusText'
  $script:owned.Refresh();$cpuBefore=$script:owned.TotalProcessorTime.TotalMilliseconds
  $watch=[Diagnostics.Stopwatch]::StartNew();Invoke-Name '开始搜索'
  $deadline=[datetime]::UtcNow.AddSeconds(120);$active=$false;$peakPrivate=0L
  do{
   $script:owned.Refresh();if($script:owned.HasExited){throw 'Owned app exited during search.'}
   $peakPrivate=[Math]::Max($peakPrivate,$script:owned.PrivateMemorySize64)
   if(-not $start.Current.IsEnabled){$active=$true}
   if($active -and $start.Current.IsEnabled){break}
   Start-Sleep -Milliseconds 25
  }while([datetime]::UtcNow -lt $deadline)
  $watch.Stop()
  if(-not $active -or -not $start.Current.IsEnabled){throw 'Owned search did not finish.'}
  $script:owned.Refresh();$cpuDelta=$script:owned.TotalProcessorTime.TotalMilliseconds-$cpuBefore
  $progressText=$progress.Current.Name;$statusText=$status.Current.Name
  Invoke-Name '说明与诊断';Start-Sleep -Milliseconds 75
  $diagnostics=(By-Id 'SearchDiagnosticsText').Current.Name
  if(($progressText -replace ',','') -notlike "*已展示文件 $ExpectedFiles · 已展示匹配 $ExpectedOccurrences ·*" -or $statusText -notlike '已完成*'){throw ('Unexpected result: '+$progressText+' / '+$statusText)}
  if($diagnostics -notmatch '本次 ([0-9,]+) ms'){throw 'Missing application duration.'}
  $internal=[double]($Matches[1] -replace ',','')
  $script:owned.Refresh()
  $row=[ordered]@{round=$round;processId=$script:owned.Id;processStartTimeUtc=$script:owned.StartTime.ToUniversalTime().ToString('o');settingsPath=$settings;passed=$true;files=$ExpectedFiles;occurrences=$ExpectedOccurrences;externalElapsedMilliseconds=$watch.Elapsed.TotalMilliseconds;endToEndMillisecondsRounded=$internal;searchCpuMilliseconds=$cpuDelta;peakWorkingSetBytesLifetime=$script:owned.PeakWorkingSet64;workingSetBytesAtEnd=$script:owned.WorkingSet64;maximumSampledPrivateBytes=$peakPrivate;progress=$progressText;status=$statusText;diagnostics=$diagnostics;ownedProcessClosed=$false}
  $script:owned.CloseMainWindow()|Out-Null
  if(-not $script:owned.WaitForExit(10000)){throw 'Owned window did not close normally.'}
  $row.ownedProcessClosed=$true;$rows.Add($row);$script:owned=$null
 }
 $report.sourceAfter=Stamp-Corpus
 if($report.sourceBefore -ne $report.sourceAfter){throw 'Source corpus changed.'}
 $report.passed=$true;$report.ownedProcessClosed=$true
}catch{$report.error=$_.Exception.Message;throw}finally{
 if($null -ne $script:owned){$script:owned.Refresh();if(-not $script:owned.HasExited){$script:owned.CloseMainWindow()|Out-Null;$script:owned.WaitForExit(10000)|Out-Null};$script:owned.Refresh();$report.ownedProcessClosed=$script:owned.HasExited}
 $report.rounds=$rows.ToArray();$file=Join-Path $out 'results.json';$report|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $file -Encoding utf8
 [ordered]@{passed=$report.passed;reportPath=$file;rounds=$rows.Count;ownedProcessClosed=$report.ownedProcessClosed;error=$report.error}|ConvertTo-Json
}
