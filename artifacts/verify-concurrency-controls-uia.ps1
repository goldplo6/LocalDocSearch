param([Parameter(Mandatory)][string]$ExePath, [Parameter(Mandatory)][ValidateSet(1,2,4)][int]$Workers, [string]$RuntimeRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe=(Resolve-Path -LiteralPath $ExePath).Path
if(-not $exe.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Expected workspace-owned executable.'}
$corpus=Join-Path $workspace 'artifacts/phase-4-corpus-standard-64/many-small'
$owned=Join-Path $workspace ('artifacts/concurrency-controls-uia-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $owned|Out-Null
function Source-State { @(Get-ChildItem -LiteralPath $corpus -File -Recurse | Sort-Object FullName | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($corpus,$_.FullName);bytes=$_.Length;ticks=$_.LastWriteTimeUtc.Ticks;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}}) | ConvertTo-Json -Compress }
$before=Source-State
$runtime='C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\6.0.11'
foreach($assembly in @('UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $runtime $assembly)}
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SearchControlNative {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
}
'@
$script:process=$null;$script:main=$null
$desc=[Windows.Automation.TreeScope]::Descendants
function Wait-Until([scriptblock]$Probe,[int]$Seconds=30){$clock=[Diagnostics.Stopwatch]::StartNew();while($clock.Elapsed.TotalSeconds -lt $Seconds){if($script:process.HasExited){throw 'Owned process exited.'};$value=& $Probe;if($value){return $value};Start-Sleep -Milliseconds 25};throw 'Search control UI condition timed out.'}
function By-Id([string]$id){$script:main.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function By-Name([string]$name){$script:main.FindFirst($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))}
function Invoke-Control($element){if($null -eq $element -or -not $element.Current.IsEnabled){throw 'Owned control missing or disabled.'};$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Set-Value($element,[string]$value){$element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)}
function Status { (By-Id 'SearchStatusText').Current.Name }
function Progress { (By-Id 'SearchProgressText').Current.Name }
function Visible-Count { $text=Progress;if($text -match '已展示(?:文件)?\s+([0-9,]+)'){return [int]($Matches[1]-replace ',','')};return 0 }
function Capture-Owned([string]$name){
 $script:process.Refresh();$handle=$script:process.MainWindowHandle;$pidValue=[uint32]0;[SearchControlNative]::GetWindowThreadProcessId($handle,[ref]$pidValue)|Out-Null
 if($pidValue -ne $script:process.Id){throw 'Screenshot ownership mismatch.'}
 $rect=[SearchControlNative+Rect]::new();if(-not [SearchControlNative]::GetWindowRect($handle,[ref]$rect)){throw 'Missing screenshot bounds.'}
 $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top);$graphics=[Drawing.Graphics]::FromImage($bitmap)
 try{$dc=$graphics.GetHdc();try{$ok=[SearchControlNative]::PrintWindow($handle,$dc,2)}finally{$graphics.ReleaseHdc($dc)};if(-not $ok){throw 'Owned window capture failed.'};$path=Join-Path $owned ($name+'.png');$bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png);return $path}finally{$graphics.Dispose();$bitmap.Dispose()}
}
function Assert-Idle {
 if(-not (By-Name '开始搜索').Current.IsEnabled -or (By-Id 'PauseResumeSearch').Current.IsEnabled -or (By-Id 'StopSearch').Current.IsEnabled){throw 'Incorrect idle command states.'}
}
function Start-OwnedSearch {
 Assert-Idle
 Invoke-Control (By-Name '开始搜索')
 Wait-Until {if(-not (By-Name '开始搜索').Current.IsEnabled -and (By-Id 'PauseResumeSearch').Current.IsEnabled -and (By-Id 'StopSearch').Current.IsEnabled){return $true}}|Out-Null
}
function Pause-OwnedSearch {
 Invoke-Control (By-Id 'PauseResumeSearch')
 $watch=[Diagnostics.Stopwatch]::StartNew()
 Wait-Until {if((Status) -like '已暂停*'){return $true}}|Out-Null
 $watch.Stop()
 if((By-Name '开始搜索').Current.IsEnabled -or -not (By-Id 'PauseResumeSearch').Current.IsEnabled -or -not (By-Id 'StopSearch').Current.IsEnabled){throw 'Incorrect paused command states.'}
 $frozenProgress=Progress;$frozenCount=Visible-Count;$samples=[Collections.Generic.List[string]]::new()
 for($i=0;$i -lt 6;$i++){Start-Sleep -Milliseconds 100;$value=Progress;$samples.Add($value);if($value -cne $frozenProgress -or (Status) -notlike '已暂停*'){throw 'Progress changed after pause acknowledgement.'}}
 return [ordered]@{requestToAcknowledgementMilliseconds=$watch.Elapsed.TotalMilliseconds;visibleFiles=$frozenCount;progress=$frozenProgress;stableForAtLeastMilliseconds=600;observations=$samples.ToArray()}
}
function Wait-Completed {
 Wait-Until {if((By-Name '开始搜索').Current.IsEnabled){return $true}} 90|Out-Null
 $progress=Progress
 if((Status) -notlike '已完成*' -or ($progress-replace ',','') -notlike '*已展示文件 5000 · 已展示匹配 10000 ·*'){throw ('Unexpected completed counts: '+$progress+' / '+(Status))}
 Assert-Idle
 return [ordered]@{status=(Status);progress=$progress;visibleFiles=(Visible-Count)}
}
function Stop-OwnedSearch {
 $countBefore=Visible-Count;$watch=[Diagnostics.Stopwatch]::StartNew()
 Invoke-Control (By-Id 'StopSearch')
 Wait-Until {if((By-Name '开始搜索').Current.IsEnabled){return $true}}|Out-Null
 $watch.Stop();Assert-Idle
 $status=Status;$count=Visible-Count
 if($status -notmatch '^已(停止|取消)' -or $count -lt $countBefore -or $count -ge 5000){throw ('Unexpected stop state: '+$status+' / '+$count)}
 $progress=Progress
 Start-Sleep -Milliseconds 250
 if((Progress) -cne $progress){throw 'Results changed after stop completion.'}
 return [ordered]@{status=$status;progress=$progress;beforeRequestVisibleFiles=$countBefore;retainedVisibleFiles=$count;requestToCompletionMilliseconds=$watch.Elapsed.TotalMilliseconds}
}
$previousDotnetRoot=$env:DOTNET_ROOT
$runtimeOverride=$null
if($RuntimeRoot){$runtimeOverride=(Resolve-Path -LiteralPath $RuntimeRoot).Path;if(-not (Test-Path -LiteralPath (Join-Path $runtimeOverride 'dotnet.exe') -PathType Leaf)){throw 'RuntimeRoot must contain dotnet.exe.'}}
$report=[ordered]@{passed=$false;workers=$Workers;exe=$exe;exeSha256=(Get-FileHash -LiteralPath $exe).Hash;runtimeRoot=$runtimeOverride;clipboardTouched=$false;globalInputUsed=$false;scenarios=@();ownedProcessClosed=$false;sourceFilesUnchanged=$false;notes='UIA invokes controls only in the owned PID; pause is acknowledged after a cooperative checkpoint and accepted results drain; this does not measure interruption inside third-party synchronous parsers.'}
$scenarios=[Collections.Generic.List[object]]::new()
try{
 $settings=Join-Path $owned 'settings.json'
 if($runtimeOverride){$env:DOTNET_ROOT=$runtimeOverride}
 $script:process=Start-Process -FilePath $exe -ArgumentList @('--settings',('"'+$settings+'"'),'--experiment-workers',[string]$Workers) -PassThru -WorkingDirectory (Split-Path $exe) -RedirectStandardOutput (Join-Path $owned 'stdout.log') -RedirectStandardError (Join-Path $owned 'stderr.log')
 Wait-Until {$script:process.Refresh();if($script:process.MainWindowHandle -ne 0){$script:main=[Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle);if($null -ne (By-Id 'PauseResumeSearch')){return $true}}}|Out-Null
 foreach($term in @('西瓜','苹果')){Set-Value (By-Name '待添加关键词') $term;Invoke-Control (By-Name '添加')}
 Set-Value (By-Name '搜索根目录') $corpus
 Start-OwnedSearch
 Wait-Until {if((Visible-Count) -gt 0){return $true}}|Out-Null
 $pause=Pause-OwnedSearch;$report.pausedScreenshot=Capture-Owned 'paused'
 Invoke-Control (By-Id 'PauseResumeSearch')
 $completed=Wait-Completed
 $scenarios.Add([ordered]@{name='pause-resume-complete';passed=$true;pause=$pause;completion=$completed})
 Start-OwnedSearch
 $pause=Pause-OwnedSearch
 $stopped=Stop-OwnedSearch
 $report.pausedStopEvidence=[ordered]@{pause=$pause;stop=$stopped}
 if($stopped.retainedVisibleFiles -ne $pause.visibleFiles){throw ('Paused stop changed displayed file set: paused='+$pause.visibleFiles+'; retained='+$stopped.retainedVisibleFiles)}
 $scenarios.Add([ordered]@{name='stop-while-paused';passed=$true;pause=$pause;stop=$stopped})
 Start-OwnedSearch
 Wait-Until {if((Visible-Count) -gt 0){return $true}}|Out-Null
 $stopped=Stop-OwnedSearch
 $scenarios.Add([ordered]@{name='stop-while-running';passed=$true;stop=$stopped})
 Start-OwnedSearch
 $completed=Wait-Completed
 $scenarios.Add([ordered]@{name='restart-after-stop';passed=$true;completion=$completed})
 $report.completedScreenshot=Capture-Owned 'completed'
 if((Source-State) -cne $before){throw 'Benchmark source changed.'}
 $report.sourceFilesUnchanged=$true;$report.passed=$true
}catch{$report.error=$_.Exception.Message;throw}finally{
 if($null -ne $script:process){$script:process.Refresh();if(-not $script:process.HasExited){$script:process.CloseMainWindow()|Out-Null;$script:process.WaitForExit(10000)|Out-Null};$script:process.Refresh();$report.ownedProcessClosed=$script:process.HasExited}
 $report.scenarios=$scenarios.ToArray();$report.reportPath=Join-Path $owned 'results.json';$report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $report.reportPath -Encoding utf8
 if($runtimeOverride){$env:DOTNET_ROOT=$previousDotnetRoot}
 [ordered]@{passed=$report.passed;workers=$Workers;exeSha256=$report.exeSha256;reportPath=$report.reportPath;scenarios=$scenarios.Count;sourceFilesUnchanged=$report.sourceFilesUnchanged;ownedProcessClosed=$report.ownedProcessClosed}|ConvertTo-Json
}
