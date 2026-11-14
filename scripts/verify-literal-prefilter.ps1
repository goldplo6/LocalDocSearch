param([Parameter(Mandatory)][string]$CandidateDirectory,[Parameter(Mandatory)][string]$MeasurementPath,[Parameter(Mandatory)][string]$NativeReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$candidate=(Resolve-Path $CandidateDirectory).Path
if(-not $candidate.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Expected workspace candidate.'}
$name=Split-Path $candidate -Leaf;$package=Join-Path $candidate $name
. (Join-Path $PSScriptRoot 'release-common.ps1')
& (Join-Path $PSScriptRoot 'release-verify.ps1') -CandidateDirectory $candidate | Out-Host
$record=Get-Content (Join-Path $package 'build-record.json') -Raw|ConvertFrom-Json
if($record.version -ne '1.0.0-rc.9' -or -not $record.selfContained -or $record.tests.Count -ne 6){throw 'Release record mismatch.'}
$total=0
foreach($test in $record.tests){
 $trx=Join-Path $candidate ('test-results/'+[IO.Path]::GetFileNameWithoutExtension($test.project)+'.trx')
 [xml]$xml=Get-Content $trx -Raw;$c=$xml.SelectSingleNode('//*[local-name()="Counters"]')
 if([int]$c.failed -ne 0 -or [int]$c.notExecuted -ne 0 -or [int]$c.passed -ne $test.passed -or $test.passed -ne $test.total){throw 'Actual TRX mismatch.'};$total+=$test.passed
}
if($total -ne $record.testsPassed){throw 'Test total mismatch.'}
foreach($f in $record.sourceInputs){if((Get-ReleaseHash (Join-Path $root $f.path)) -ne $f.sha256){throw ('Source drift: '+$f.path)}}
$locks=@($record.sourceInputs|Where-Object path -Match '(^|/)packages\.lock\.json$');if($locks.Count -ne 13){throw 'Lock count mismatch.'}
$m=Get-Content $MeasurementPath -Raw|ConvertFrom-Json
if(-not $m.passed -or $m.groups.Count -ne 60 -or $m.rows.Count -ne 30){throw 'Incomplete matrix.'}
$signatures=@{};$perf=0;$verified=0
foreach($g in $m.groups){
 $r=Get-Content $g.report -Raw|ConvertFrom-Json
 if(-not $r.passed -or $r.sourceSha256Before -ne $r.sourceSha256After -or $r.rounds.Count -ne 3){throw 'Raw report mismatch.'}
 $v=$r.resultVerification
 if(-not $v.passed -or $v.phase -ne 'verification-excluded' -or $v.resultContentSha256.Length -ne 64 -or $v.resultOrderSha256.Length -ne 64){throw 'Missing verification.'}
 $key=@($v.status,$v.scannedFiles,$v.matchedFiles,$v.occurrences,$v.resultContentSha256,$v.resultOrderSha256,$r.sourceSha256Before)-join '|'
 if($signatures.ContainsKey($g.scenario)){if($signatures[$g.scenario] -ne $key){throw 'Result/order changed.'}}else{$signatures[$g.scenario]=$key}
 foreach($round in $r.rounds){if(-not $round.passed -or 'resultContentSha256' -in $round.PSObject.Properties.Name -or 'resultOrderSha256' -in $round.PSObject.Properties.Name){throw 'Contaminated sample.'};$perf++};$verified++
}
foreach($f in $m.binaryFiles){if((Get-ReleaseHash (Join-Path (Join-Path $m.output $f.version) $f.path)) -ne $f.sha256.ToLowerInvariant()){throw 'Measured binary drift.'}}
$n=Get-Content $NativeReportPath -Raw|ConvertFrom-Json
if(-not $n.passed -or -not $n.ownedProcessClosed -or -not $n.sourceFilesUnchanged -or $n.cases.Count -ne 7 -or -not $n.preview.passed -or $n.clipboardTouched -or $n.globalInputUsed){throw 'Native validation failed.'}
if($n.exeSha256 -ne (Get-ReleaseHash (Join-Path $package 'DocSearch.exe')) -or $n.docSearchDllSha256 -ne (Get-ReleaseHash (Join-Path $package 'DocSearch.dll'))){throw 'Native hash mismatch.'}
$production=Join-Path $root 'artifacts/root-relocated/LocalDocSearch/DocSearch.exe'
if((Get-ReleaseHash $production) -ne $m.productionSha256Before.ToLowerInvariant() -or $m.productionSha256Before -ne $m.productionSha256After){throw 'Existing product changed.'}
$documents=@('README.md','docs/literal-prefilter-plan.md','docs/literal-prefilter-results.md','docs/search-principles-and-next-steps.md');$links=0
foreach($doc in $documents){$path=Join-Path $root $doc;$text=Get-Content $path -Raw;foreach($match in [regex]::Matches($text,'\]\(([^)]+)\)')){$target=$match.Groups[1].Value.Trim('<','>');if($target -match '^(https?://|#)'){continue};$target=($target -split '#')[0];if($target -and -not(Test-Path -LiteralPath (Join-Path (Split-Path $path) $target))){throw ('Broken link: '+$doc+' -> '+$target)};$links++}}
$remaining=@(Get-CimInstance Win32_Process -Filter "Name='DocSearch.exe'"|Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($candidate,[StringComparison]::OrdinalIgnoreCase)});if($remaining.Count){throw 'Owned process remains.'}
$out=Join-Path $candidate 'final-audit.json';if(Test-Path $out){throw 'Audit already exists.'}
$a=[ordered]@{passed=$true;checkedAtUtc=[datetime]::UtcNow.ToString('O');candidate=$candidate;testsPassed=$total;tests=$record.tests;sourceFiles=$record.sourceInputs.Count;sourceLocks=$locks.Count;sourceUnchanged=$true;performanceSamples=$perf;independentVerifications=$verified;scenarios=$signatures.Count;measurement=(Resolve-Path $MeasurementPath).Path;nativeReport=(Resolve-Path $NativeReportPath).Path;nativeSearchCases=$n.cases.Count;previewPassed=$true;sourceDocumentsUnchanged=$true;ownedCandidateProcesses=0;documents=$documents;relativeLinks=$links;productionDirectoryUpdated=$false;zipSha256=Get-ReleaseHash (Join-Path $candidate ($name+'.zip'));executable=Join-Path $package 'DocSearch.exe';executableSha256=Get-ReleaseHash (Join-Path $package 'DocSearch.exe')}
Write-ReleaseJson $out $a
$a|ConvertTo-Json -Depth 12
