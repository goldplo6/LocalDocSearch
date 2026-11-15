param([Parameter(Mandatory=$true)][string]$ExePath,[Parameter(Mandatory=$true)][string]$Label,[int]$Repeat=3,[Parameter(Mandatory=$true)][ValidateSet(1,2,4)][int]$Workers,[string]$RuntimeRoot)
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path (Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))) '..')).Path
$exe=(Resolve-Path -LiteralPath $ExePath).Path
if(-not $exe.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Benchmark EXE must belong to this workspace.'}
if($Repeat -lt 1 -or $Repeat -gt 5){throw 'Repeat must be 1..5.'}
$runtime='C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\6.0.11'
Add-Type -Path (Join-Path $runtime 'UIAutomationTypes.dll')
Add-Type -Path (Join-Path $runtime 'UIAutomationClient.dll')
$output=Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))) ('concurrency-ui-'+$Label+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
$rows=[Collections.Generic.List[object]]::new()
$script:owned=$null
function Find-Id([string]$id){
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $element=$script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if($null -eq $element){
  $pidCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:owned.Id)
  foreach($root in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$pidCondition)){
   $element=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
   if($null -ne $element){break}
  }
 }
 if($null -eq $element){throw ('Missing automation ID: '+$id)}
 return $element
}
function Find-Name([string]$name){
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
 $element=$script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if($null -eq $element){throw ('Missing owned control: '+$name)}
 return $element
}
function Invoke-Name([string]$name){(Find-Name $name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Set-Value([string]$name,[string]$value){(Find-Name $name).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)}
$previousDotnetRoot=$env:DOTNET_ROOT
$runtimeOverride=$null
if($RuntimeRoot){$runtimeOverride=(Resolve-Path -LiteralPath $RuntimeRoot).Path;if(-not (Test-Path -LiteralPath (Join-Path $runtimeOverride 'dotnet.exe') -PathType Leaf)){throw 'RuntimeRoot must contain dotnet.exe.'}}
$report=[ordered]@{schemaVersion=1;label=$Label;workers=$Workers;runtimeRoot=$runtimeOverride;exe=$exe;exeSha256=(Get-FileHash -LiteralPath $exe).Hash;repeat=$Repeat;passed=$false;clipboardTouched=$false;rounds=@();error=$null;ownedProcessClosed=$false;notes='Independent owned process each round; UIA overhead included in external timing; OS cache not cleared; no cold-start claim; no global input.'}
try{
 if($runtimeOverride){$env:DOTNET_ROOT=$runtimeOverride}
 for($round=1;$round -le $Repeat;$round++){
  $settings=Join-Path $output ('settings-'+$round+'.json')
  $script:owned=Start-Process -FilePath $exe -ArgumentList @('--settings',('"'+$settings+'"'),'--experiment-workers',[string]$Workers) -PassThru -WorkingDirectory (Split-Path $exe) -RedirectStandardOutput (Join-Path $output ('stdout-'+$round+'.log')) -RedirectStandardError (Join-Path $output ('stderr-'+$round+'.log'))
  $deadline=[datetime]::UtcNow.AddSeconds(30)
  do{$script:owned.Refresh();if($script:owned.HasExited){throw 'Owned process exited before window creation.'};if($script:owned.MainWindowHandle -ne 0){break};Start-Sleep -Milliseconds 50}while([datetime]::UtcNow -lt $deadline)
  if($script:owned.MainWindowHandle -eq 0){throw 'Owned window did not appear.'}
  $script:window=[Windows.Automation.AutomationElement]::FromHandle($script:owned.MainWindowHandle)
  foreach($term in @('西瓜','苹果')){Set-Value '待添加关键词' $term;Invoke-Name '添加'}
  Set-Value '搜索根目录' (Join-Path $workspace 'artifacts/phase-4-corpus-standard-64/many-small')
  $start=Find-Name '开始搜索';$progressElement=Find-Id 'SearchProgressText';$statusElement=Find-Id 'SearchStatusText'
  $watch=[Diagnostics.Stopwatch]::StartNew();Invoke-Name '开始搜索'
  $deadline=[datetime]::UtcNow.AddSeconds(90);$observedActive=$false;$peakPrivate=0L
  do{
   $script:owned.Refresh();if($script:owned.HasExited){throw 'Owned process exited during search.'}
   $peakPrivate=[Math]::Max($peakPrivate,$script:owned.PrivateMemorySize64)
   $enabled=$start.Current.IsEnabled
   if(-not $enabled){$observedActive=$true}
   if($observedActive -and $enabled){break}
   Start-Sleep -Milliseconds 25
  }while([datetime]::UtcNow -lt $deadline)
  $watch.Stop()
  if(-not $observedActive -or -not $start.Current.IsEnabled){throw 'Owned search did not complete.'}
  $progress=$progressElement.Current.Name;$status=$statusElement.Current.Name
  Invoke-Name '说明与诊断';Start-Sleep -Milliseconds 75
  $diagnostics=(Find-Id 'SearchDiagnosticsText').Current.Name
  if(($progress -replace ',','') -notlike '*已展示文件 5000 · 已展示匹配 10000 ·*' -or $status -notlike '已完成*'){throw ('Unexpected result: '+$progress+' / '+$status)}
  if($diagnostics -notmatch '本次 ([0-9,]+) ms'){throw ('Missing internal duration: '+$diagnostics)}
  $internal=[double]($Matches[1] -replace ',','')
  $script:owned.Refresh()
  $row=[ordered]@{round=$round;passed=$true;files=5000;occurrences=10000;externalElapsedMilliseconds=$watch.Elapsed.TotalMilliseconds;endToEndMillisecondsRounded=$internal;peakWorkingSetBytesLifetime=$script:owned.PeakWorkingSet64;maximumSampledPrivateBytes=$peakPrivate;totalProcessorMilliseconds=$script:owned.TotalProcessorTime.TotalMilliseconds;progress=$progress;status=$status;diagnostics=$diagnostics;ownedProcessClosed=$false}
  $script:owned.CloseMainWindow()|Out-Null
  if(-not $script:owned.WaitForExit(10000)){throw 'Owned window failed to close normally.'}
  $row.ownedProcessClosed=$true;$rows.Add($row);$script:owned=$null
 }
 $report.rounds=$rows.ToArray();$report.passed=$true;$report.ownedProcessClosed=$true
}catch{$report.error=$_.Exception.Message;throw}
finally{
 if($runtimeOverride){$env:DOTNET_ROOT=$previousDotnetRoot}
 if($null -ne $script:owned){$script:owned.Refresh();if(-not $script:owned.HasExited){$script:owned.CloseMainWindow()|Out-Null;$script:owned.WaitForExit(10000)|Out-Null};$script:owned.Refresh();$report.ownedProcessClosed=$script:owned.HasExited}
 $report.rounds=$rows.ToArray();$reportPath=Join-Path $output 'results.json';$report|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $reportPath -Encoding utf8
 [ordered]@{passed=$report.passed;reportPath=$reportPath;rounds=$rows.Count;ownedProcessClosed=$report.ownedProcessClosed;error=$report.error}|ConvertTo-Json
}
