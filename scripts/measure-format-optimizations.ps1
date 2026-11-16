param(
 [Parameter(Mandatory=$true)][string]$DriverDirectory,
 [Parameter(Mandatory=$true)][string]$CandidateDirectory,
 [string]$BaselineDirectory,
 [string]$OutputDirectory,
 [string[]]$Scenarios=@(),
 [switch]$Quick,
 [switch]$ObserveRetained,
 [ValidateRange(10,3600)][int]$TimeoutSeconds=900
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
if(-not $BaselineDirectory){$BaselineDirectory=Join-Path $root 'artifacts/releases-ui-csv/LocalDocSearch-1.0.0-rc.11-win-x64/LocalDocSearch-1.0.0-rc.11-win-x64'}
$driver=(Resolve-Path -LiteralPath $DriverDirectory).Path
$candidate=(Resolve-Path -LiteralPath $CandidateDirectory).Path
$baseline=(Resolve-Path -LiteralPath $BaselineDirectory).Path
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/format-measurement-'+[guid]::NewGuid().ToString('N'))}
$out=[IO.Path]::GetFullPath($OutputDirectory)
function Ordinary([string]$path){
 $cursor=[IO.Path]::GetFullPath($path)
 while($cursor){
  if(Test-Path -LiteralPath $cursor){if((Get-Item -Force -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked paths are excluded.'}}
  $parent=Split-Path -Parent $cursor
  if($parent -eq $cursor){break};$cursor=$parent
 }
}
foreach($path in @($driver,$candidate,$baseline,$out)){
 if(-not $path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Measurement paths must belong to this workspace.'}
 Ordinary $path
}
foreach($inputDirectory in @($driver,$candidate,$baseline)){
 if($out.Equals($inputDirectory,[StringComparison]::OrdinalIgnoreCase) -or $out.StartsWith($inputDirectory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be outside all inputs.'}
}
if(Test-Path -LiteralPath $out){throw 'Output must be new.'}
foreach($file in @((Join-Path $driver 'LocalDocSearch.Benchmarks.dll'),(Join-Path $driver 'LocalDocSearch.Benchmarks.runtimeconfig.json'),$dotnet)){if(-not (Test-Path -LiteralPath $file)){throw 'Required driver/runtime input missing.'}}
New-Item -ItemType Directory -Path $out|Out-Null
$summaryPath=Join-Path $out 'summary.json'
$report=[ordered]@{
 schemaVersion=1;passed=$false;startedUtc=[datetime]::UtcNow.ToString('o');finishedUtc=$null
 baseline=$baseline;candidate=$candidate;driver=$driver;output=$out;quick=[bool]$Quick
 binaryFiles=@();groups=@();rows=@();comparisons=@();protectedBefore=@();protectedAfter=@();sourceBefore=@();sourceAfter=@();corpusBefore=@();corpusAfter=@();error=$null
 semantics='Serialized ABBA four groups, independent process per scenario/group, repeat=3; six timed samples per version/scenario. Shared driver/dependencies copied byte-for-byte, only actual Core and Extractors DLLs replaced. Counts-only timing with full independent ordered SHA verification after EACH round. Source length/mtime/SHA outside timing. All positive and negative raw samples retained; medians recomputed from saved raw JSON. OS cache not cleared; NOT cold-cache. Lifetime peak includes preceding verification and observations. Retained observation is managed live bytes during consumption, NOT extraction peak; forced GC only outside timing. No absolute performance acceptance threshold.'
}
function Hash([string]$path){Ordinary $path;(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function NewText([string]$path,[string]$text){
 Ordinary $path
 $s=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes($text);$s.Write($bytes,0,$bytes.Length)}finally{$s.Dispose()}
}
function Save {
 $json=$report|ConvertTo-Json -Depth 24
 if(-not (Test-Path -LiteralPath $summaryPath)){NewText $summaryPath $json}else{Ordinary $summaryPath;[IO.File]::WriteAllText($summaryPath,$json,[Text.UTF8Encoding]::new($false))}
}
function Median($values){
 $a=@($values|Sort-Object);if(-not $a.Count){throw 'Empty samples.'}
 if($a.Count%2){return [double]$a[[int]($a.Count/2)]}
 return ([double]$a[$a.Count/2-1]+[double]$a[$a.Count/2])/2
}
function Inventory([string]$directory){
 Ordinary $directory
 foreach($entry in Get-ChildItem -LiteralPath $directory -Force -Recurse){
  Ordinary $entry.FullName
  if(-not $entry.PSIsContainer){[ordered]@{path=[IO.Path]::GetRelativePath($directory,$entry.FullName).Replace([char]92,'/');bytes=$entry.Length;mtime=$entry.LastWriteTimeUtc.Ticks;sha256=Hash $entry.FullName}}
 }
}
function Protected {
 foreach($version in @('baseline','candidate','driver')){
  $directory=switch($version){'baseline'{$baseline};'candidate'{$candidate};'driver'{$driver}}
  foreach($file in @(Inventory $directory)|Sort-Object { $_.path }){[ordered]@{input=$version;path=$file.path;bytes=$file.bytes;mtime=$file.mtime;sha256=$file.sha256}}
 }
 # Release archives beside the extracted baseline are controls as well.
 foreach($archive in Get-ChildItem -LiteralPath (Split-Path -Parent $baseline) -Filter '*.zip' -File|Sort-Object FullName){
  [ordered]@{input='baseline-archive';path=$archive.Name;bytes=$archive.Length;mtime=$archive.LastWriteTimeUtc.Ticks;sha256=Hash $archive.FullName}
 }
}
function Sources {
 # Explicit project roots only: no unrelated user directories or local credentials.
 $paths=@(& git -C $root ls-files --cached --others --exclude-standard -- src tools tests scripts Directory.Build.props Directory.Build.targets Directory.Packages.props global.json NuGet.Config)
 if($LASTEXITCODE -ne 0){throw 'Source inventory failed.'}
 foreach($relative in $paths|Sort-Object){
  $path=Join-Path $root $relative
  if(Test-Path -LiteralPath $path -PathType Leaf){$info=Get-Item -LiteralPath $path;[ordered]@{path=$relative;bytes=$info.Length;mtime=$info.LastWriteTimeUtc.Ticks;sha256=Hash $path}}
 }
}
function EqualInventory($left,$right,[string]$code){if(($left|ConvertTo-Json -Depth 8 -Compress) -cne ($right|ConvertTo-Json -Depth 8 -Compress)){throw $code}}
function Run([string]$version,[string[]]$arguments,[string]$stem){
 $psi=[Diagnostics.ProcessStartInfo]::new($dotnet)
 $psi.WorkingDirectory=$root;$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.CreateNoWindow=$true
 foreach($argument in (@((Join-Path $out "$version/LocalDocSearch.Benchmarks.dll"))+$arguments)){$psi.ArgumentList.Add($argument)}
 $process=[Diagnostics.Process]::new();$process.StartInfo=$psi;$started=$false
 try{
  $started=$process.Start();$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  $timedOut=-not $process.WaitForExit($TimeoutSeconds*1000);if($timedOut){$process.Kill($true);$process.WaitForExit()}
  $text=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
  NewText (Join-Path $out "$stem.json") $text;NewText (Join-Path $out "$stem.stderr.log") $errorText
  if($timedOut){throw "Owned benchmark timed out: $stem"};if($process.ExitCode -ne 0){throw "Driver returned $($process.ExitCode): $stem"}
  return $text|ConvertFrom-Json
 }finally{if($started -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
}
function Signature($verification){
 $verification|Select-Object directCounts,engineCounts,segmentSha256,expectedSegmentSha256,extractionWarningsSha256,expectedExtractionWarningsSha256,engineResultSha256,expectedEngineResultSha256,engineIssuesSha256,expectedEngineIssuesSha256,engineSummarySha256,expectedEngineSummarySha256,sourceBefore,sourceAfter|ConvertTo-Json -Depth 16 -Compress
}
try{
 $report.protectedBefore=@(Protected);$report.sourceBefore=@(Sources)
 $allowed=@('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll')
 foreach($version in @('baseline','candidate')){
  $dest=Join-Path $out $version;New-Item -ItemType Directory -Path $dest|Out-Null
  Get-ChildItem -LiteralPath $driver -Force|Copy-Item -Destination $dest -Recurse
  $libraries=if($version -eq 'baseline'){$baseline}else{$candidate}
  foreach($name in $allowed){Copy-Item -LiteralPath (Join-Path $libraries $name) -Destination (Join-Path $dest $name)}
 }
 $report.binaryFiles=@(foreach($version in @('baseline','candidate')){foreach($file in @(Inventory (Join-Path $out $version))|Sort-Object { $_.path }){[ordered]@{version=$version;path=$file.path;bytes=$file.bytes;sha256=$file.sha256}}})
 $left=@($report.binaryFiles|Where-Object version -eq 'baseline');$right=@($report.binaryFiles|Where-Object version -eq 'candidate')
 if($left.Count -ne $right.Count){throw 'Driver inventory count differs.'}
 foreach($file in $left){
  $other=@($right|Where-Object path -eq $file.path)
  if($other.Count -ne 1){throw 'Driver inventory membership differs.'}
  if($file.path -notin $allowed -and ($file.sha256 -ne $other[0].sha256 -or $file.bytes -ne $other[0].bytes)){throw 'Unexpected shared driver/dependency difference.'}
 }
 $corpus=Join-Path $out 'format-corpus';$prepareArgs=@('format-prepare',$corpus);if($Quick){$prepareArgs+=@('--quick')}
 $prepared=Run 'candidate' $prepareArgs 'prepare'
 if(-not $prepared.passed){throw 'Fixture preparation failed.'}
 $available=@($prepared.scenarios)
 if(-not $Scenarios.Count){$Scenarios=$available}
 $Scenarios=@($Scenarios|ForEach-Object {$_ -split ','})
 if(@($Scenarios|Select-Object -Unique).Count -ne $Scenarios.Count){throw 'Duplicate scenarios.'}
 foreach($scenario in $Scenarios){if($scenario -cnotin $available){throw 'Unknown scenario filter.'}}
 $report.corpusBefore=@(Inventory $corpus|Sort-Object { $_.path })
 $reference=@{};$sequence=@('baseline','candidate','candidate','baseline')
 for($group=0;$group -lt 4;$group++){
  $version=$sequence[$group]
  foreach($scenario in $Scenarios){
   $arguments=@('format-run',$corpus,$scenario,'--repeat','3')
   if($ObserveRetained -and $scenario -match '^direct-(xlsx-|docx-|pptx$|pdf$)'){$arguments+=@('--observe-retained')}
   $stem="g$group-$version-$scenario";$r=Run $version $arguments $stem
   if(-not $r.passed -or @($r.rounds).Count -ne 3 -or @($r.verifications).Count -ne 3 -or $r.scenario -cne $scenario){throw 'Invalid scenario report.'}
   foreach($pair in @(@('loadedCoreSha256','LocalDocSearch.Core.dll'),@('loadedExtractorsSha256','LocalDocSearch.Extractors.dll'),@('driverSha256','LocalDocSearch.Benchmarks.dll'))){
    $expectedHash=($report.binaryFiles|Where-Object {$_.version -eq $version -and $_.path -eq $pair[1]}).sha256
    if($r.($pair[0]) -ne $expectedHash){throw 'Loaded assembly hash differs from staged assembly.'}
   }
   if($r.sourceSha256Before -ne $r.sourceSha256After){throw 'Source manifest drift.'}
   $signature=$null
   foreach($v in $r.verifications){
    if(-not $v.passed -or $v.phase -cne 'after-round-excluded'){throw 'Independent verification failed.'}
    foreach($pair in @(@('segmentSha256','expectedSegmentSha256'),@('extractionWarningsSha256','expectedExtractionWarningsSha256'),@('engineResultSha256','expectedEngineResultSha256'),@('engineIssuesSha256','expectedEngineIssuesSha256'),@('engineSummarySha256','expectedEngineSummarySha256'))){
     if($v.($pair[0]) -notmatch '^[A-F0-9]{64}$' -or $v.($pair[0]) -cne $v.($pair[1])){throw 'Ordered semantic digest mismatch.'}
    }
    EqualInventory $v.sourceBefore $v.sourceAfter 'Per-round source drift.'
    $s=Signature $v
    if($null -eq $signature){$signature=$s}elseif($signature -cne $s){throw 'Within-process semantics changed.'}
   }
   if(-not $reference.ContainsKey($scenario)){$reference[$scenario]=$signature}elseif($reference[$scenario] -cne $signature){throw 'ABBA cross-version semantics changed.'}
   foreach($round in $r.rounds){
    if(-not $round.passed){throw 'Timed counts failed.'}
    $verification=$r.verifications|Where-Object round -eq $round.round
    $expectedCounts=if($r.expected.mode -eq 'direct'){$verification.directCounts}else{$verification.engineCounts}
    EqualInventory $round.counts $expectedCounts 'Timed counts differ from excluded verification.'
   }
   if($arguments -contains '--observe-retained'){
    if(-not $r.retainedObservation.passed -or $r.retainedObservation.phase -cne 'retained-consumption-excluded'){throw 'Retained observation did not enumerate the complete fixture.'}
   }
   $report.groups+=@([ordered]@{group=$group;version=$version;scenario=$scenario;report=(Join-Path $out "$stem.json")});Save
   Write-Host "Completed $stem"
  }
 }
 foreach($scenario in $Scenarios){foreach($version in @('baseline','candidate')){
  $samples=@(foreach($group in $report.groups|Where-Object {$_.version -eq $version -and $_.scenario -ceq $scenario}){(Get-Content -LiteralPath $group.report -Raw|ConvertFrom-Json).rounds})
  if($samples.Count -ne 6){throw 'Expected six samples per version and scenario.'}
  $report.rows+=@([ordered]@{scenario=$scenario;version=$version;samples=$samples.Count;elapsedMs=Median $samples.elapsedMilliseconds;cpuMs=Median $samples.cpuMilliseconds;allocatedBytes=Median $samples.memory.totalAllocatedBytesDelta;peakWorkingSetBytesLifetime=Median $samples.memory.processPeakWorkingSetBytesLifetime;elapsedRaw=@($samples.elapsedMilliseconds);cpuRaw=@($samples.cpuMilliseconds);allocatedRaw=@($samples.memory.totalAllocatedBytesDelta);peakRaw=@($samples.memory.processPeakWorkingSetBytesLifetime)})
 }}
 foreach($scenario in $Scenarios){
  $a=$report.rows|Where-Object {$_.version -eq 'baseline' -and $_.scenario -ceq $scenario};$b=$report.rows|Where-Object {$_.version -eq 'candidate' -and $_.scenario -ceq $scenario}
  $report.comparisons+=@([ordered]@{scenario=$scenario;elapsedDeltaPercent=if($a.elapsedMs){100*($b.elapsedMs/$a.elapsedMs-1)}else{$null};cpuDeltaPercent=if($a.cpuMs){100*($b.cpuMs/$a.cpuMs-1)}else{$null};allocatedDeltaBytes=$b.allocatedBytes-$a.allocatedBytes;peakWorkingSetLifetimeDeltaBytes=$b.peakWorkingSetBytesLifetime-$a.peakWorkingSetBytesLifetime;interpretation='Negative delta indicates decrease; all deltas retained without a performance pass/fail gate.'})
 }
 foreach($version in @('baseline','candidate')){
  $current=@(Inventory (Join-Path $out $version)|Sort-Object { $_.path })
  $original=@($report.binaryFiles|Where-Object version -eq $version|Sort-Object { $_.path })
  if($current.Count -ne $original.Count){throw 'Staged binary membership drift.'}
  for($i=0;$i -lt $current.Count;$i++){if($current[$i].path -cne $original[$i].path -or $current[$i].sha256 -ne $original[$i].sha256 -or $current[$i].bytes -ne $original[$i].bytes){throw 'Staged binary drift.'}}
 }
 $report.passed=$true
}catch{$report.error=$_.Exception.Message}finally{
 try{
  $report.protectedAfter=@(Protected);EqualInventory $report.protectedBefore $report.protectedAfter 'Protected input/package drift.'
  $report.sourceAfter=@(Sources);EqualInventory $report.sourceBefore $report.sourceAfter 'Tracked source drift.'
  if(Test-Path -LiteralPath (Join-Path $out 'format-corpus')){$report.corpusAfter=@(Inventory (Join-Path $out 'format-corpus')|Sort-Object { $_.path });EqualInventory $report.corpusBefore $report.corpusAfter 'Corpus drift.'}
 }catch{$report.passed=$false;$report.error=$_.Exception.Message}
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');Save
 [ordered]@{passed=$report.passed;summaryPath=$summaryPath;error=$report.error}|ConvertTo-Json
}
if(-not $report.passed){throw $report.error}
