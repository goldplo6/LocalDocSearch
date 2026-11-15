param(
 [Parameter(Mandatory=$true)][string]$DriverDirectory,
 [Parameter(Mandatory=$true)][string]$CandidatePackageDirectory,
 [string]$OutputDirectory,
 [ValidateRange(10,900)][int]$TimeoutSeconds=300
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$baseline=Join-Path $root 'artifacts/releases-fixed-overhead/LocalDocSearch-1.0.0-rc.10-win-x64/LocalDocSearch-1.0.0-rc.10-win-x64'
$driver=(Resolve-Path -LiteralPath $DriverDirectory).Path
$candidate=(Resolve-Path -LiteralPath $CandidatePackageDirectory).Path
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/csv-parsing-measurement-'+[guid]::NewGuid().ToString('N'))}
$out=[IO.Path]::GetFullPath($OutputDirectory)
foreach($path in @($driver,$candidate,$out)){if(-not $path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Measurement paths must belong to the workspace.'}}
if(Test-Path -LiteralPath $out){throw 'Output must be new.'}
New-Item -ItemType Directory -Path $out|Out-Null
$report=[ordered]@{passed=$false;startedUtc=[datetime]::UtcNow.ToString('o');baseline=$baseline;candidate=$candidate;driver=$driver;output=$out;binaryFiles=@();groups=@();rows=@();protectedBefore=@();protectedAfter=@();error=$null;semantics='Serialized ABBA, repeat=3 in independent command processes; six timed samples per version and workload. Verification hashes excluded from timers. OS cache not cleared. Allocation is GC process delta; peak working set is lifetime cumulative including preceding verification. Direct extraction includes IO and decoding.'}
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Save {$report|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding utf8}
function Median($values){$a=@($values|Sort-Object);if(-not $a.Count){throw 'Empty samples.'};if($a.Count%2){return [double]$a[[int]($a.Count/2)]};return ([double]$a[$a.Count/2-1]+[double]$a[$a.Count/2])/2}
function Protect {
 foreach($path in @((Join-Path $baseline 'LocalDocSearch.Core.dll'),(Join-Path $candidate 'LocalDocSearch.Core.dll'),(Join-Path $baseline 'DocSearch.exe'),(Join-Path $root 'artifacts/root-relocated/LocalDocSearch/DocSearch.exe'),(Join-Path (Split-Path $baseline) 'LocalDocSearch-1.0.0-rc.10-win-x64.zip'))){[ordered]@{path=$path;sha256=Hash $path}}
}
function Run([string]$version,[string[]]$arguments,[string]$stem){
 $psi=[Diagnostics.ProcessStartInfo]::new($dotnet);$psi.WorkingDirectory=$root;$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.CreateNoWindow=$true
 foreach($arg in (@((Join-Path $out "$version/LocalDocSearch.Benchmarks.dll"))+$arguments)){$psi.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$psi;$started=$false
 try{
  $started=$p.Start();$stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
  if(-not $p.WaitForExit($TimeoutSeconds*1000)){$p.Kill($true);$p.WaitForExit();throw "Owned driver timed out: $stem"}
  $text=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult()
  [IO.File]::WriteAllText((Join-Path $out "$stem.json"),$text);[IO.File]::WriteAllText((Join-Path $out "$stem.stderr.log"),$err)
  if($p.ExitCode -ne 0){throw "Driver failed $($p.ExitCode): $stem"}
  return $text|ConvertFrom-Json
 }finally{if($started -and -not $p.HasExited){$p.Kill($true);$p.WaitForExit()};$p.Dispose()}
}
try{
 $report.protectedBefore=@(Protect)
 foreach($version in @('old','new')){
  $dest=Join-Path $out $version;New-Item -ItemType Directory -Path $dest|Out-Null
  Get-ChildItem -LiteralPath $driver -Force|Copy-Item -Destination $dest -Recurse
  $library=if($version -eq 'old'){Join-Path $baseline 'LocalDocSearch.Core.dll'}else{Join-Path $candidate 'LocalDocSearch.Core.dll'}
  Copy-Item -LiteralPath $library -Destination (Join-Path $dest 'LocalDocSearch.Core.dll')
 }
 $report.binaryFiles=@(foreach($version in @('old','new')){foreach($file in Get-ChildItem -LiteralPath (Join-Path $out $version) -File -Recurse){[ordered]@{version=$version;path=[IO.Path]::GetRelativePath((Join-Path $out $version),$file.FullName);sha256=Hash $file.FullName}}})
 foreach($file in $report.binaryFiles|Where-Object {$_.version -eq 'old' -and $_.path -ne 'LocalDocSearch.Core.dll'}){
  $other=@($report.binaryFiles|Where-Object {$_.version -eq 'new' -and $_.path -eq $file.path})
  if($other.Count -ne 1 -or $other[0].sha256 -ne $file.sha256){throw 'Unexpected non-Core binary difference.'}
 }
 $csv=Join-Path $out 'csv-corpus';$prepared=Run 'new' @('csv-prepare',$csv) 'prepare'
 if(-not $prepared.passed){throw 'CSV preparation failed.'}
 $commands=@(foreach($scenario in @('direct-plain','direct-quoted','direct-boundary','engine-sparse-plain','engine-sparse-quoted','engine-sparse-boundary','engine-none-plain','engine-none-quoted','engine-none-boundary')){[ordered]@{kind='csv';scenario=$scenario;args=@('csv-run',$csv,$scenario,'--repeat','3')}})
 foreach($scenario in @('many-small','mixed')){$commands+=@([ordered]@{kind='control';scenario=$scenario;args=@('run',(Join-Path $root 'artifacts/phase-4-corpus-standard-64'),$scenario,'--repeat','3','--workers','1')})}
 $reference=@{};$sequence=@('old','new','new','old')
 for($group=0;$group -lt 4;$group++){
  $version=$sequence[$group]
  foreach($command in $commands){
   $key=$command.kind+'-'+$command.scenario;$stem="g$group-$version-$key";$r=Run $version $command.args $stem
   if(-not $r.passed -or @($r.rounds).Count -ne 3){throw "Invalid report: $stem"}
   if($command.kind -eq 'csv'){
    $core=($report.binaryFiles|Where-Object {$_.version -eq $version -and $_.path -eq 'LocalDocSearch.Core.dll'}).sha256
    $driverHash=($report.binaryFiles|Where-Object {$_.version -eq $version -and $_.path -eq 'LocalDocSearch.Benchmarks.dll'}).sha256
    if($r.coreSha256 -ne $core -or $r.driverSha256 -ne $driverHash -or $r.sourceSha256Before -ne $r.sourceSha256After){throw 'CSV loaded binary or source integrity mismatch.'}
    if(@($r.verifications).Count -ne 3){throw 'Expected three excluded verifications.'}
    $signature=$null
    foreach($v in $r.verifications){
     if(-not $v.passed -or $v.phase -ne 'after-round-excluded' -or $v.segmentSha256.Length -ne 64 -or $v.segmentSha256 -ne $v.expectedSegmentSha256 -or $v.engineResultSha256.Length -ne 64 -or $v.engineResultSha256 -ne $v.expectedEngineResultSha256){throw 'CSV semantic verification failed.'}
     $s=($v|Select-Object segmentSha256,extractionWarningsSha256,engineResultSha256,engineIssuesSha256,directCounts,engineCounts|ConvertTo-Json -Depth 10 -Compress)
     if($null -eq $signature){$signature=$s}elseif($signature -cne $s){throw 'CSV round semantics changed.'}
    }
   }else{
    $v=$r.resultVerification
    $driverHash=($report.binaryFiles|Where-Object {$_.version -eq $version -and $_.path -eq 'LocalDocSearch.Benchmarks.dll'}).sha256
    if(-not $v.passed -or $v.phase -ne 'verification-excluded' -or -not $r.integrity.verifiedBeforeAndAfter -or $r.binarySha256 -ne $driverHash -or $v.resultContentSha256.Length -ne 64 -or $v.resultOrderSha256.Length -ne 64 -or $r.integrity.sourceSha256Before -ne $r.integrity.sourceSha256After){throw 'Control verification failed.'}
    $signature=($v|Select-Object status,scannedFiles,matchedFiles,occurrences,publishedFiles,publishedOccurrences,errorCount,warningCount,suppressedIssueCount,issueDetailsObserved,limitReasonCode,resultOrderSha256,resultContentSha256|ConvertTo-Json -Compress)+'|'+$r.integrity.sourceSha256Before
    foreach($round in $r.rounds){foreach($field in @('status','scannedFiles','matchedFiles','occurrences','publishedFiles','publishedOccurrences','errorCount','warningCount','suppressedIssueCount','issueDetailsObserved')){if($round.$field -ne $v.$field){throw 'Timed/control counts differ.'}}}
   }
   if(-not $reference.ContainsKey($key)){$reference[$key]=$signature}elseif($reference[$key] -cne $signature){throw "Old/new result mismatch: $key"}
   foreach($round in $r.rounds){if(-not $round.passed){throw 'Performance round failed.'}}
   $report.groups+=@([ordered]@{group=$group;version=$version;kind=$command.kind;scenario=$command.scenario;report=(Join-Path $out "$stem.json")});Save
   Write-Host "Completed $stem"
  }
 }
 foreach($command in $commands){foreach($version in @('old','new')){
  $samples=@(foreach($group in $report.groups|Where-Object {$_.version -eq $version -and $_.kind -eq $command.kind -and $_.scenario -eq $command.scenario}){(Get-Content -LiteralPath $group.report -Raw|ConvertFrom-Json).rounds})
  if($samples.Count -ne 6){throw 'Expected six samples.'}
  $report.rows+=@([ordered]@{kind=$command.kind;scenario=$command.scenario;version=$version;samples=6;elapsedMs=Median $samples.elapsedMilliseconds;cpuMs=Median $samples.cpuMilliseconds;allocatedBytes=Median $samples.memory.totalAllocatedBytesDelta;peakWorkingSetBytesLifetime=Median $samples.memory.processPeakWorkingSetBytesLifetime;elapsedRaw=@($samples.elapsedMilliseconds);cpuRaw=@($samples.cpuMilliseconds);allocatedRaw=@($samples.memory.totalAllocatedBytesDelta);peakRaw=@($samples.memory.processPeakWorkingSetBytesLifetime)})
 }}
 foreach($file in $report.binaryFiles){if((Hash (Join-Path (Join-Path $out $file.version) $file.path)) -ne $file.sha256){throw 'Driver binary drift.'}}
 $report.passed=$true
}catch{$report.error=$_.Exception.Message;throw}finally{
 try{$report.protectedAfter=@(Protect);if(($report.protectedBefore|ConvertTo-Json -Compress) -cne ($report.protectedAfter|ConvertTo-Json -Compress)){$report.passed=$false;$report.error='Protected binary drift.'}}catch{$report.passed=$false;$report.error=$_.Exception.Message}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');Save
 [ordered]@{passed=$report.passed;summaryPath=(Join-Path $out 'summary.json');error=$report.error}|ConvertTo-Json
}
if(-not $report.passed){throw $report.error}
