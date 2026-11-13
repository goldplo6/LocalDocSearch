#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildRecordPath,[Parameter(Mandatory)][string]$CliSummaryPath,[Parameter(Mandatory)][string]$DesktopSummaryPath,[Parameter(Mandatory)][string]$MachinePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$build=Get-Content -LiteralPath $BuildRecordPath -Raw|ConvertFrom-Json
$cli=Get-Content -LiteralPath $CliSummaryPath -Raw|ConvertFrom-Json
$desktop=Get-Content -LiteralPath $DesktopSummaryPath -Raw|ConvertFrom-Json
$machine=Get-Content -LiteralPath $MachinePath -Raw|ConvertFrom-Json
function Check([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
Check ($build.passed -and $cli.passed -and $desktop.passed) 'Every experiment phase must pass.'
Check ($build.defaultWorkers -eq 1 -and -not $build.productionDirectoryUpdated -and $desktop.deliveredExeUnchanged) 'Default or delivered application changed.'
$inputPath=Join-Path $build.outputRoot 'source-inputs.json'
$inputs=@(Get-Content -LiteralPath $inputPath -Raw|ConvertFrom-Json)
Check ($inputs.Count -eq 189 -and @($inputs|Where-Object path -match '(^|/)packages\.lock\.json$').Count -eq 13) 'Unexpected source/lock inventory.'
foreach($entry in $inputs){Check ((Sha (Join-Path $root $entry.path)) -eq $entry.sha256 -and (Sha (Join-Path $build.snapshot $entry.path)) -eq $entry.sha256) ('Source drift: '+$entry.path)}
$testTotal=0;$testEvidence=@()
$trxs=@(Get-ChildItem -LiteralPath (Join-Path $build.outputRoot 'test-results') -Filter '*.trx' -File)
Check ($trxs.Count -eq 6) 'Expected six regression result files.'
foreach($file in $trxs){[xml]$xml=Get-Content -LiteralPath $file.FullName -Raw;$counter=$xml.SelectSingleNode('//*[local-name()="Counters"]');Check ($null -ne $counter -and [int]$counter.total -eq [int]$counter.passed -and [int]$counter.failed -eq 0 -and [int]$counter.notExecuted -eq 0) 'Regression failure or skipped case.';$testTotal += [int]$counter.passed;$testEvidence+=@{name=$file.Name;passed=[int]$counter.passed;sha256=Sha $file.FullName}}
Check ($testTotal -eq 1247 -and $build.testsPassed -eq 1247) 'Expected 1247 complete regression cases.'
Check ((Sha $build.benchmarkDll) -eq $build.benchmarkSha256 -and $cli.binarySha256 -eq $build.benchmarkSha256) 'Benchmark binary mismatch.'
Check ((Sha $build.desktopExe) -eq $build.desktopSha256 -and (Sha (Join-Path (Split-Path $build.desktopExe) 'DocSearch.dll')) -eq $build.desktopDllSha256 -and $desktop.exeSha256 -eq $build.desktopSha256 -and $desktop.desktopDllSha256 -eq $build.desktopDllSha256) 'Desktop binary mismatch.'
foreach($a in $cli.assemblies){Check ((Sha (Join-Path (Split-Path $build.benchmarkDll) $a.name)) -eq $a.sha256) 'CLI dependency changed.'}
foreach($name in @('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll')){Check ((Sha (Join-Path (Split-Path $build.desktopExe) $name)) -eq (Sha (Join-Path (Split-Path $build.benchmarkDll) $name))) 'UI and CLI engine dependencies differ.'}
Check ((@($cli.workersSequence) -join ',') -eq '1,2,4,4,2,1' -and (@($desktop.workersSequence) -join ',') -eq '1,2,4,4,2,1') 'Worker sequence changed.'
Check (@($cli.measurements).Count -eq 15 -and @($cli.controls).Count -eq 18 -and @($cli.correctness).Count -eq 9) 'Incomplete CLI matrix.'
foreach($m in $cli.measurements){Check ($m.elapsedMilliseconds.count -eq 6 -and $m.totalAllocatedBytesDelta.count -eq 6 -and $m.lifetimePeakWorkingSetBytes.count -eq 6) 'Incomplete CLI distribution.'}
$raw=@(Get-ChildItem -LiteralPath (Split-Path $CliSummaryPath) -Filter '*.json' -File|Where-Object BaseName -ne 'summary')
Check ($raw.Count -eq 45) 'Expected 45 raw CLI process reports.'
$baseline=@{};$performance=0;$verification=0;$extra=0;$controls=0
foreach($file in $raw){
 $r=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
 Check ($r.passed -and $r.binarySha256 -eq $build.benchmarkSha256) 'Raw CLI binary identity failure.'
 $isControl=$file.BaseName.StartsWith('control-')
 if($isControl){Check ($r.sourceIntegrity -and $r.sourceSha256Before -eq $cli.sourceSha256 -and $r.sourceSha256After -eq $cli.sourceSha256) 'Raw CLI control integrity failure.'}
 else{Check ($r.integrity.verifiedBeforeAndAfter -and $r.integrity.sourceSha256Before -eq $cli.sourceSha256 -and $r.integrity.sourceSha256After -eq $cli.sourceSha256) 'Raw CLI run integrity failure.'}
 foreach($round in $r.rounds){Check ($round.passed -and @($round.mismatches).Count -eq 0) 'Raw CLI round failure.'}
 if($isControl){$controls+=@($r.rounds).Count;continue}
 $v=$r.resultVerification
 Check ($v.passed -and $v.phase -eq 'verification-excluded' -and @($v.mismatches).Count -eq 0) 'Missing independent content verification.'
 $digest=$v.resultOrderSha256+':'+$v.resultContentSha256
 if($baseline.ContainsKey($r.scenario)){Check ($digest -ceq $baseline[$r.scenario]) 'Result content or publication order changed.'}else{$baseline[$r.scenario]=$digest}
 $verification++
 if($file.BaseName.StartsWith('timing-')){$performance+=@($r.rounds).Count}else{$extra+=@($r.rounds).Count}
}
Check ($performance -eq 90 -and $verification -eq 39 -and $extra -eq 9 -and $controls -eq 18 -and $baseline.Count -eq 8) 'Raw measurement totals mismatch.'
Check (@($desktop.controls).Count -eq 3 -and @($desktop.groups).Count -eq 6 -and @($desktop.measurements).Count -eq 3 -and $desktop.sourceUnchanged -and -not $desktop.clipboardTouched -and -not $desktop.globalInputUsed) 'Incomplete native desktop matrix.'
$nativeCases=0;$nativeRounds=0
foreach($e in $desktop.controls){Check ((Sha $e.reportPath) -eq $e.reportSha256) 'Native control report drift.';$r=Get-Content -LiteralPath $e.reportPath -Raw|ConvertFrom-Json;Check ($r.passed -and $r.ownedProcessClosed -and $r.sourceFilesUnchanged -and @($r.scenarios).Count -eq 4) 'Native control failure.';foreach($s in $r.scenarios){Check $s.passed 'Native control scenario failed.';$nativeCases++}}
foreach($e in $desktop.groups){Check ((Sha $e.reportPath) -eq $e.reportSha256) 'UI timing report drift.';$r=Get-Content -LiteralPath $e.reportPath -Raw|ConvertFrom-Json;Check ($r.passed -and $r.ownedProcessClosed -and @($r.rounds).Count -eq 3) 'UI timing group failed.';foreach($s in $r.rounds){Check ($s.passed -and $s.ownedProcessClosed -and $s.files -eq 5000 -and $s.occurrences -eq 10000) 'UI timing incomplete.';$nativeRounds++}}
Check ($nativeCases -eq 12 -and $nativeRounds -eq 18) 'Native totals mismatch.'
Check ((Sha 'C:\LocalDocSearch\DocSearch.exe') -eq $build.shallowExeSha256Before) 'Delivered application changed.'
$documents=@('README.md','docs/bounded-concurrency-plan.md','docs/bounded-concurrency-results.md','docs/search-principles-and-next-steps.md')
$links=0;$documentEvidence=@()
foreach($relative in $documents){$path=Join-Path $root $relative;$text=Get-Content -LiteralPath $path -Raw;foreach($match in [regex]::Matches($text,'(?<!!)\[[^\]]+\]\((?<target>[^)]+)\)')){$target=$match.Groups['target'].Value.Trim('<','>');if($target -match '^(https?://|#|mailto:)'){continue};$target=($target -split '#',2)[0];if(-not $target){continue};$target=[uri]::UnescapeDataString($target);Check (Test-Path -LiteralPath (Join-Path (Split-Path $path) $target)) ('Broken document link: '+$relative+' -> '+$target);$links++};$documentEvidence+=@{path=$relative;sha256=Sha $path}}
$exeDirectory=Split-Path $build.desktopExe
$active=@(Get-CimInstance Win32_Process -Filter "Name='DocSearch.exe'"|Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($exeDirectory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)})
Check ($active.Count -eq 0) 'An experimental application process remains.'
$result=[ordered]@{passed=$true;testsPassed=$testTotal;testEvidence=$testEvidence;sourceFiles=$inputs.Count;lockFiles=13;sourceAndSnapshotUnchanged=$true;defaultWorkers=1;productionDirectoryUpdated=$false;benchmarkSha256=$build.benchmarkSha256;desktopSha256=$build.desktopSha256;desktopDllSha256=$build.desktopDllSha256;performanceSamples=$performance;independentContentVerifications=$verification;extraSemanticSamples=$extra;controlMeasurements=$controls;nativeControlCases=$nativeCases;nativeTimingRuns=$nativeRounds;ownedProcessesRemaining=$active.Count;documents=$documentEvidence;relativeLinksChecked=$links;brokenLinks=0;buildRecordPath=(Resolve-Path $BuildRecordPath).Path;cliSummaryPath=(Resolve-Path $CliSummaryPath).Path;desktopSummaryPath=(Resolve-Path $DesktopSummaryPath).Path;machinePath=(Resolve-Path $MachinePath).Path;machineSha256=Sha $MachinePath;verifiedUtc=[datetime]::UtcNow.ToString('O')}
$output=Join-Path $build.outputRoot 'final-audit.json'
Check (-not (Test-Path -LiteralPath $output)) 'Preserve existing final audit evidence.'
$result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $output -Encoding utf8
$result|ConvertTo-Json -Depth 12
