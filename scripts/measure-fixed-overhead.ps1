param(
 [Parameter(Mandatory=$true)][string]$DriverDirectory,
 [string]$CandidatePackageDirectory,
 [string]$CorpusDirectory,
 [string]$OutputDirectory,
 [ValidateRange(1,3600)][int]$TimeoutSeconds=300
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$pinnedCommit='79f9f314f0bfe658842b9bc6ea6e60066f737d5e'
$baselineRoot=Join-Path $root 'artifacts/releases-literal-prefilter/LocalDocSearch-1.0.0-rc.9-win-x64'
$buildRecord=Join-Path $baselineRoot 'LocalDocSearch-1.0.0-rc.9-win-x64/build-record.json'
if(-not $CorpusDirectory){$CorpusDirectory=Join-Path $root 'artifacts/phase-4-corpus-standard-64'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/fixed-overhead-measurement-'+[guid]::NewGuid().ToString('N'))}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Output directory must be new.'}
New-Item -ItemType Directory -Path $out | Out-Null
$summary=Join-Path $out 'summary.json'
$report=[ordered]@{schemaVersion=1;passed=$false;startedUtc=[datetime]::UtcNow.ToString('O');output=$out;sourceCommit=$pinnedCommit;baselineBuildRecord=$buildRecord;source=@{};binaryFiles=@();groups=@();rows=@();comparisons=@();protectedBefore=@();protectedAfter=@();cache='OS cache not cleared; ABBA groups serialized; each command fresh process, each command repeat=3';verification='separate result hashes excluded from performance loops; coordinates compare every observation including failure outputs and loop totals';peakSemantics='process lifetime peak, never per-round incremental peak';error=$null}
function Save-Summary { $report | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $summary -Encoding utf8 }
function Hash-File([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Median($values){$a=@($values|Sort-Object);if($a.Count -eq 0){throw 'No measurement samples.'};if($a.Count%2){return [double]$a[[int]($a.Count/2)]};return ([double]$a[$a.Count/2-1]+[double]$a[$a.Count/2])/2}
function Protected-Hashes {
 @('artifacts/root-relocated/LocalDocSearch/DocSearch.exe',
   'artifacts/releases-literal-prefilter/LocalDocSearch-1.0.0-rc.9-win-x64/LocalDocSearch-1.0.0-rc.9-win-x64.zip',
   'artifacts/releases-literal-prefilter/LocalDocSearch-1.0.0-rc.9-win-x64/LocalDocSearch-1.0.0-rc.9-win-x64/DocSearch.exe') | ForEach-Object {
   $path=Join-Path $root $_
   [ordered]@{path=$path;sha256=Hash-File $path}
 }
}
function Git-InputHash([string]$relative){
 $psi=[Diagnostics.ProcessStartInfo]::new('git');$psi.WorkingDirectory=$root;$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 foreach($arg in @('cat-file','blob',($pinnedCommit+':'+$relative))){$psi.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$psi
 $bytes=[IO.MemoryStream]::new()
 try{
  [void]$p.Start();$err=$p.StandardError.ReadToEndAsync();$p.StandardOutput.BaseStream.CopyTo($bytes);$p.WaitForExit()
  if($p.ExitCode -ne 0){throw ('Pinned source missing: '+$relative+' '+$err.GetAwaiter().GetResult())}
  return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes.ToArray()))
 }finally{$bytes.Dispose();$p.Dispose()}
}
function Invoke-Driver([string]$version,[string[]]$arguments,[string]$stem){
 $dll=Join-Path $out "$version/LocalDocSearch.Benchmarks.dll"
 $stdout=Join-Path $out "$stem.stdout.json";$stderr=Join-Path $out "$stem.stderr.log"
 $psi=[Diagnostics.ProcessStartInfo]::new($dotnet);$psi.WorkingDirectory=$root;$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.CreateNoWindow=$true
 foreach($arg in (@($dll)+$arguments)){$psi.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$psi;$started=$false;$timedOut=$false
 try{
  $started=$p.Start();$stdoutTask=$p.StandardOutput.ReadToEndAsync();$stderrTask=$p.StandardError.ReadToEndAsync()
  if(-not $p.WaitForExit($TimeoutSeconds*1000)){$timedOut=$true;$p.Kill($true);$p.WaitForExit()}
  $text=$stdoutTask.GetAwaiter().GetResult();$errors=$stderrTask.GetAwaiter().GetResult()
  [IO.File]::WriteAllText($stdout,$text);[IO.File]::WriteAllText($stderr,$errors)
  if($timedOut){throw "Owned driver timed out: $stem"}
  if($p.ExitCode -ne 0){throw "Driver exited $($p.ExitCode): $stem"}
  return $text|ConvertFrom-Json
 }finally{if($started -and -not $p.HasExited){$p.Kill($true);$p.WaitForExit()};$p.Dispose()}
}
function Assert-BinaryFiles {
 foreach($file in $report.binaryFiles){if((Hash-File (Join-Path (Join-Path $out $file.version) $file.path)) -ne $file.sha256){throw 'Measurement binary drift.'}}
}
try{
 $report.protectedBefore=@(Protected-Hashes)
 $driver=(Resolve-Path -LiteralPath $DriverDirectory).Path
 $corpus=(Resolve-Path -LiteralPath $CorpusDirectory).Path
 $record=Get-Content -LiteralPath $buildRecord -Raw|ConvertFrom-Json
 if($record.version -ne '1.0.0-rc.9' -or $record.testsPassed -ne 1453){throw 'Unexpected baseline record.'}
 # This rc9 record has sourceInputs, not a source.snapshot property; record the observed sibling build-input path.
 $snapshot=(Resolve-Path -LiteralPath (Join-Path $baselineRoot 'build-input')).Path
 $inputs=@($record.sourceInputs | Where-Object {$_.path -like 'src/LocalDocSearch.Core/*' -or $_.path -like 'src/LocalDocSearch.Extractors/*' -or $_.path -in @('.editorconfig','Directory.Build.props','Directory.Packages.props','global.json','NuGet.Config')})
 if(@($inputs|Where-Object {$_.path -like 'src/LocalDocSearch.Core/*'}).Count -lt 10 -or @($inputs|Where-Object {$_.path -like 'src/LocalDocSearch.Extractors/*'}).Count -lt 5){throw 'Incomplete library source input inventory.'}
 $verified=@(foreach($inputFile in $inputs){
  $pinnedHash=Git-InputHash $inputFile.path
  if($pinnedHash -ne $inputFile.sha256){throw ('Baseline source input differs from pinned commit: '+$inputFile.path)}
  $copy=Join-Path $snapshot $inputFile.path;$expected=$inputFile.sha256
  # RID resolve replaces these lock copies, explicitly recorded separately by the release build.
  $rid=@($record.candidateRidLocks|Where-Object {$_.path -eq $inputFile.path})
  if($rid.Count -eq 1){$expected=$rid[0].sha256}
  $actual=Hash-File $copy
  if($actual -ne $expected){throw ('Baseline snapshot input drift: '+$inputFile.path)}
  [ordered]@{path=$inputFile.path;sourceSha256=$inputFile.sha256;pinnedSha256=$pinnedHash;snapshotSha256=$actual;candidateRidLock=($rid.Count -eq 1)}
 })
 # Use the shipped rc9 libraries rather than its pre-publish benchmark copies.
 $oldBin=Split-Path $buildRecord
 $report.source=@{snapshot=$snapshot;recordField='sourceInputs';verifiedInputs=$verified;oldBinaryDirectory=$oldBin;newSourceHeadRequirement='none; candidate may include uncommitted authorized edits'}
 foreach($version in @('old','new')){
  $dest=Join-Path $out $version;New-Item -ItemType Directory -Path $dest|Out-Null
  Get-ChildItem -LiteralPath $driver -Force|Copy-Item -Destination $dest -Recurse
 }
 foreach($library in @('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll')){Copy-Item -LiteralPath (Join-Path $oldBin $library) -Destination (Join-Path $out "old/$library")}
 if($CandidatePackageDirectory){
  $package=(Resolve-Path -LiteralPath $CandidatePackageDirectory).Path
  foreach($library in @('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll')){Copy-Item -LiteralPath (Join-Path $package $library) -Destination (Join-Path $out "new/$library")}
  $report.source.candidatePackageDirectory=$package
 }
 $report.binaryFiles=@(foreach($version in @('old','new')){foreach($file in Get-ChildItem -LiteralPath (Join-Path $out $version) -File -Recurse){
  [ordered]@{version=$version;path=[IO.Path]::GetRelativePath((Join-Path $out $version),$file.FullName);sha256=Hash-File $file.FullName}
 }})
 foreach($file in @($report.binaryFiles|Where-Object {$_.version -eq 'old' -and $_.path -notin @('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll')})){
  $other=@($report.binaryFiles|Where-Object {$_.version -eq 'new' -and $_.path -eq $file.path})
  if($other.Count -ne 1 -or $other[0].sha256 -ne $file.sha256){throw 'Unexpected non-library driver difference.'}
 }
 $report.driverDirectory=$driver;$report.corpus=$corpus
 $literalCorpus=Join-Path $out 'literal-corpus'
 $prepared=Invoke-Driver 'new' @('literal-prepare',$literalCorpus) 'prepare-literal'
 if(-not $prepared.passed){throw 'Literal preparation failed.'}
 $commands=@([ordered]@{kind='coordinates';scenario='coordinates';arguments=@('overhead-coordinates','--repeat','3','--iterations','500000')})
 foreach($scenario in @('many-small','large-log','mixed','dense-file','global-budget')){
  $commands+=@([ordered]@{kind='run';scenario=$scenario;arguments=@('run',$corpus,$scenario,'--repeat','3','--workers','1')})
 }
 foreach($scenario in @('short-16','short-32','short-64','none-32','none-64')){
  if($scenario -notin $prepared.scenarios){throw "Required literal scenario absent: $scenario"}
  $commands+=@([ordered]@{kind='literal';scenario=$scenario;arguments=@('literal-run',$literalCorpus,$scenario,'--repeat','3')})
 }
 $report.literalCorpus=$literalCorpus;$reference=@{};$sequence=@('old','new','new','old')
 for($group=0;$group -lt $sequence.Count;$group++){
  $version=$sequence[$group]
  foreach($command in $commands){
   $key=$command.kind+'-'+$command.scenario;$stem="g$group-$version-$key"
   $r=Invoke-Driver $version $command.arguments $stem
   if(-not $r.passed -or @($r.rounds).Count -ne 3){throw "Invalid measurement report: $stem"}
   $driverSha=($report.binaryFiles|Where-Object {$_.version -eq $version -and $_.path -eq 'LocalDocSearch.Benchmarks.dll'}).sha256
   if($command.kind -eq 'coordinates'){
    foreach($name in @('driver','core','extractors')){
     $binary=if($name -eq 'driver'){'LocalDocSearch.Benchmarks.dll'}elseif($name -eq 'core'){'LocalDocSearch.Core.dll'}else{'LocalDocSearch.Extractors.dll'}
     $expected=($report.binaryFiles|Where-Object {$_.version -eq $version -and $_.path -eq $binary}).sha256
     if($r.($name+'Sha256') -ne $expected){throw 'Loaded coordinate binary mismatch.'}
    }
    if($r.iterations -ne 500000 -or $r.repeat -ne 3){throw 'Incorrect coordinate workload.'}
    $signature=@($r.inputsSha256,($r.resultVerification|ConvertTo-Json -Depth 6 -Compress),($r.rounds[0]|Select-Object calls,successCount,sum,checksum|ConvertTo-Json -Compress)) -join '|'
    foreach($round in $r.rounds){
     if(-not $round.passed -or $round.calls -ne 500000 -or $round.sum -ne $r.rounds[0].sum -or $round.successCount -ne $r.rounds[0].successCount -or $round.checksum -ne $r.rounds[0].checksum){throw 'Coordinate loop validation failed.'}
    }
   }else{
    $v=$r.resultVerification
    if(-not $v.passed -or $v.phase -ne 'verification-excluded' -or $v.resultContentSha256.Length -ne 64 -or $v.resultOrderSha256.Length -ne 64){throw 'Missing excluded verification hashes.'}
    if($command.kind -eq 'run'){
     if(-not $r.integrity.verifiedBeforeAndAfter -or $r.binarySha256 -ne $driverSha){throw 'CLI integrity or driver mismatch.'}
     $before=$r.integrity.sourceSha256Before;$after=$r.integrity.sourceSha256After
    }else{
     $coreSha=($report.binaryFiles|Where-Object {$_.version -eq $version -and $_.path -eq 'LocalDocSearch.Core.dll'}).sha256
     if($r.driverSha256 -ne $driverSha -or $r.coreSha256 -ne $coreSha){throw 'Loaded literal binary mismatch.'}
     $before=$r.sourceSha256Before;$after=$r.sourceSha256After
    }
    if($before.Length -ne 64 -or $before -ne $after){throw 'Source changed during measurement.'}
    $semanticFields=@('status','scannedFiles','matchedFiles','occurrences','publishedFiles','publishedOccurrences','errorCount','warningCount','suppressedIssueCount','issueDetailsObserved','limitReasonCode','resultOrderSha256','resultContentSha256')
    $signature=($v|Select-Object -Property $semanticFields|ConvertTo-Json -Compress)+'|'+$before
    foreach($round in $r.rounds){
     if(-not $round.passed -or 'resultContentSha256' -in $round.PSObject.Properties.Name -or 'resultOrderSha256' -in $round.PSObject.Properties.Name){throw 'Performance loop failed or includes result hashing.'}
     foreach($field in @('status','scannedFiles','matchedFiles','occurrences','publishedFiles','publishedOccurrences','errorCount','warningCount','suppressedIssueCount','issueDetailsObserved')){
      if($round.$field -ne $v.$field){throw "Performance/verification counts differ: $field"}
     }
    }
   }
   if(-not $reference.ContainsKey($key)){$reference[$key]=$signature}elseif($reference[$key] -cne $signature){throw "Old/new semantic mismatch: $key"}
   $report.groups+=@([ordered]@{group=$group;version=$version;kind=$command.kind;scenario=$command.scenario;report=(Join-Path $out "$stem.stdout.json");machine=$r.machine})
   Save-Summary
   Write-Host "Completed $stem"
  }
 }
 foreach($command in $commands){foreach($version in @('old','new')){
  $samples=@(foreach($group in $report.groups|Where-Object {$_.kind -eq $command.kind -and $_.scenario -eq $command.scenario -and $_.version -eq $version}){(Get-Content -LiteralPath $group.report -Raw|ConvertFrom-Json).rounds})
  if($samples.Count -ne 6){throw 'Expected six ABBA samples per version and command.'}
  $alloc=@(foreach($sample in $samples){if($command.kind -eq 'coordinates'){$sample.totalAllocatedBytesDelta}else{$sample.memory.totalAllocatedBytesDelta}})
  $peaks=@(foreach($sample in $samples){if($command.kind -eq 'coordinates'){$sample.processPeakWorkingSetBytesLifetime}else{$sample.memory.processPeakWorkingSetBytesLifetime}})
  $working=@(foreach($sample in $samples){if($command.kind -eq 'coordinates'){$sample.processWorkingSetBytesAfter}else{$sample.memory.workingSetBytesAtEnd}})
  $report.rows+=@([ordered]@{kind=$command.kind;scenario=$command.scenario;version=$version;samples=6;elapsedMs=Median $samples.elapsedMilliseconds;cpuMs=Median $samples.cpuMilliseconds;allocatedBytes=Median $alloc;workingSetBytes=Median $working;peakWorkingSetBytesLifetime=Median $peaks;elapsedRaw=@($samples.elapsedMilliseconds);cpuRaw=@($samples.cpuMilliseconds);allocatedRaw=$alloc;workingSetRaw=$working;peakRaw=$peaks})
 }}
 foreach($command in $commands){
  $old=($report.rows|Where-Object {$_.kind -eq $command.kind -and $_.scenario -eq $command.scenario -and $_.version -eq 'old'})
  $new=($report.rows|Where-Object {$_.kind -eq $command.kind -and $_.scenario -eq $command.scenario -and $_.version -eq 'new'})
  $report.comparisons+=@([ordered]@{kind=$command.kind;scenario=$command.scenario;elapsedDeltaMs=($new.elapsedMs-$old.elapsedMs);cpuDeltaMs=($new.cpuMs-$old.cpuMs);allocatedDeltaBytes=($new.allocatedBytes-$old.allocatedBytes);workingSetDeltaBytes=($new.workingSetBytes-$old.workingSetBytes);peakLifetimeDeltaBytes=($new.peakWorkingSetBytesLifetime-$old.peakWorkingSetBytesLifetime)})
 }
 Assert-BinaryFiles
 $report.passed=$true
}catch{$report.error=$_.Exception.Message;throw}finally{
 try{
  $report.protectedAfter=@(Protected-Hashes)
  if(($report.protectedBefore|ConvertTo-Json -Compress) -cne ($report.protectedAfter|ConvertTo-Json -Compress)){$report.passed=$false;$report.error='Protected production/package hash drift.'}
 }catch{$report.passed=$false;$report.error='Protected hash verification failed: '+$_.Exception.Message}
 $report.finishedUtc=[datetime]::UtcNow.ToString('O');Save-Summary
 Write-Output ([pscustomobject]@{passed=$report.passed;summaryPath=$summary}|ConvertTo-Json -Compress)
}
if(-not $report.passed){throw $report.error}
