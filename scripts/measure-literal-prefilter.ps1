param([Parameter(Mandatory=$true)][string]$DriverDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$env:DOTNET_CLI_HOME=Join-Path $root '.tools/dotnet-home';$env:NUGET_PACKAGES=Join-Path $root '.tools/nuget/packages'
$baselineRoot=Join-Path $root 'artifacts/concurrency-validation-96049d547b78403c919c0a4e1b094408'
$baseline=Get-Content (Join-Path $baselineRoot 'build-record.json') -Raw|ConvertFrom-Json
if(-not $baseline.passed -or $baseline.testsPassed -ne 1247){throw 'Baseline was not verified.'}
$oldCore=Join-Path (Split-Path $baseline.benchmarkDll) 'LocalDocSearch.Core.dll'
if((Get-FileHash $oldCore).Hash -ne 'E0816FE2384FDAC4A695CFEC312BFE1248322FC48EC5419D12F77BBBA574121C'){throw 'Baseline Core drift.'}
foreach($inputFile in @(Get-Content (Join-Path $baselineRoot 'source-inputs.json') -Raw|ConvertFrom-Json|Where-Object {$_.path -like 'src/LocalDocSearch.Core/*'})){
 $copy=Join-Path $baseline.snapshot $inputFile.path
 if((Get-FileHash $copy).Hash -ne $inputFile.sha256){throw 'Baseline source drift.'}
 $blob=(& git hash-object --no-filters $copy|Out-String).Trim();$baselineBlob=(& git rev-parse ('c0acb8a9f12ba5489c250ce6348bc1556a9b3b38:'+$inputFile.path)|Out-String).Trim()
 if($LASTEXITCODE -ne 0 -or $blob -ne $baselineBlob){throw 'Baseline source differs from the verified baseline commit.'}
}
$driver=(Resolve-Path -LiteralPath $DriverDirectory).Path
$out=Join-Path $root ('artifacts/literal-prefilter-measurement-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out|Out-Null
$production=Join-Path $root 'artifacts/root-relocated/LocalDocSearch/DocSearch.exe'
$productionSha=(Get-FileHash $production).Hash
foreach($name in @('old','new')){
 $dest=Join-Path $out $name;New-Item -ItemType Directory $dest|Out-Null
 Get-ChildItem -LiteralPath $driver -Force|Copy-Item -Destination $dest -Recurse
}
Copy-Item -LiteralPath $oldCore -Destination (Join-Path $out 'old/LocalDocSearch.Core.dll')
$hashes=@(foreach($name in @('old','new')){foreach($f in Get-ChildItem (Join-Path $out $name) -File -Recurse){[ordered]@{version=$name;path=[IO.Path]::GetRelativePath((Join-Path $out $name),$f.FullName);sha256=(Get-FileHash $f.FullName).Hash}}})
foreach($f in @($hashes|Where-Object {$_.version -eq 'old' -and $_.path -ne 'LocalDocSearch.Core.dll'})){
 $other=@($hashes|Where-Object {$_.version -eq 'new' -and $_.path -eq $f.path})
 if($other.Count -ne 1 -or $other[0].sha256 -ne $f.sha256){throw 'Non-Core driver difference.'}
}
$report=[ordered]@{passed=$false;startedUtc=[datetime]::UtcNow.ToString('O');output=$out;driverDirectory=$driver;baselineBuild=(Join-Path $baselineRoot 'build-record.json');binaryFiles=$hashes;groups=@();rows=@();productionSha256Before=$productionSha;cache='OS cache not cleared; source verification outside search timing';verification='separate actual same-query search excluded from performance';machine=Get-CimInstance Win32_Processor|Select-Object Name,NumberOfCores,NumberOfLogicalProcessors}
function Invoke-Driver([string]$version,[string[]]$arguments,[string]$stem){
 $dll=Join-Path $out "$version/LocalDocSearch.Benchmarks.dll"
 $stdout=Join-Path $out "$stem.stdout.json";$stderr=Join-Path $out "$stem.stderr.log"
 $all=@($dll)+$arguments
 & $dotnet @all 1> $stdout 2> $stderr
 if($LASTEXITCODE -ne 0){throw "Driver failed: $stem (see stderr)."}
 return Get-Content -LiteralPath $stdout -Raw|ConvertFrom-Json
}
function Median($values){$a=@($values|Sort-Object);if($a.Count%2){return [double]$a[[int]($a.Count/2)]};return ([double]$a[$a.Count/2-1]+[double]$a[$a.Count/2])/2}
try{
 $corpus=Join-Path $out 'corpus'
 $prepared=Invoke-Driver 'new' @('literal-prepare',$corpus) 'prepare'
 if(-not $prepared.passed){throw 'Prepare failed.'}
 $scenarios=@($prepared.scenarios);$report.corpus=$corpus
 $reference=@{};$sequence=@('old','new','new','old')
 for($g=0;$g -lt $sequence.Count;$g++){
  $version=$sequence[$g]
  foreach($scenario in $scenarios){
   $stem="g$g-$version-$scenario"
   $r=Invoke-Driver $version @('literal-run',$corpus,$scenario,'--repeat','3') $stem
   if(-not $r.passed -or $r.sourceSha256Before -ne $r.sourceSha256After -or $r.rounds.Count -ne 3){throw "Invalid report: $stem"}
   $v=$r.resultVerification
   if(-not $v.passed -or $v.phase -ne 'verification-excluded' -or $v.resultContentSha256.Length -ne 64 -or $v.resultOrderSha256.Length -ne 64){throw 'Missing independent verification.'}
   $signature=@($v.status,$v.scannedFiles,$v.matchedFiles,$v.occurrences,$v.resultOrderSha256,$v.resultContentSha256,$r.sourceSha256Before) -join '|'
   if(-not $reference.ContainsKey($scenario)){$reference[$scenario]=$signature}elseif($reference[$scenario] -ne $signature){throw "Semantic difference: $scenario"}
   foreach($round in $r.rounds){if(-not $round.passed -or ('resultContentSha256' -in $round.PSObject.Properties.Name) -or ('resultOrderSha256' -in $round.PSObject.Properties.Name)){throw 'Performance round contaminated or failed.'}}
   $expectedCore=($hashes|Where-Object {$_.version -eq $version -and $_.path -eq 'LocalDocSearch.Core.dll'}).sha256
   $expectedDriver=($hashes|Where-Object {$_.version -eq $version -and $_.path -eq 'LocalDocSearch.Benchmarks.dll'}).sha256
   if($r.coreSha256 -ne $expectedCore -or $r.driverSha256 -ne $expectedDriver){throw 'Loaded binary mismatch.'}
   $report.groups+=@([ordered]@{group=$g;version=$version;scenario=$scenario;report=(Join-Path $out "$stem.stdout.json")})
   Write-Host "Completed $stem"
  }
 }
 foreach($scenario in $scenarios){foreach($version in @('old','new')){
  $samples=@(foreach($group in $report.groups|Where-Object {$_.scenario -eq $scenario -and $_.version -eq $version}){(Get-Content $group.report -Raw|ConvertFrom-Json).rounds})
  if($samples.Count -ne 6){throw 'Wrong sample count.'}
  $report.rows+=@([ordered]@{scenario=$scenario;version=$version;samples=6;elapsedMs=Median $samples.elapsedMilliseconds;minMs=($samples.elapsedMilliseconds|Measure-Object -Minimum).Minimum;maxMs=($samples.elapsedMilliseconds|Measure-Object -Maximum).Maximum;cpuMs=Median $samples.cpuMilliseconds;allocatedBytes=Median $samples.memory.totalAllocatedBytesDelta;peakWorkingSetBytesLifetime=Median $samples.memory.processPeakWorkingSetBytesLifetime;elapsedRaw=@($samples.elapsedMilliseconds)})
 }}
 foreach($f in $hashes){if((Get-FileHash (Join-Path (Join-Path $out $f.version) $f.path)).Hash -ne $f.sha256){throw 'Measurement binary drift.'}}
 $report.productionSha256After=(Get-FileHash $production).Hash
 if($report.productionSha256After -ne $productionSha){throw 'Existing package drift.'}
 $report.passed=$true
}finally{$report.finishedUtc=[datetime]::UtcNow.ToString('O');$report|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding utf8}
$report.rows|ForEach-Object {[pscustomobject]$_}|Format-Table scenario,version,samples,elapsedMs,cpuMs,allocatedBytes
Write-Host ('Summary: '+(Join-Path $out 'summary.json'))
