param(
 [Parameter(Mandatory=$true)][string]$CandidateExe,
 [Parameter(Mandatory=$true)][string]$DenseCorpusDirectory,
 [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$old=Join-Path $root 'artifacts/releases-fixed-overhead/LocalDocSearch-1.0.0-rc.10-win-x64/LocalDocSearch-1.0.0-rc.10-win-x64/DocSearch.exe'
$new=(Resolve-Path -LiteralPath $CandidateExe).Path
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/ui-csv-delivery-'+[guid]::NewGuid().ToString('N'))}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must belong to workspace.'}
if(Test-Path -LiteralPath $out){throw 'Output must be new.'}
New-Item -ItemType Directory -Path $out|Out-Null
$report=[ordered]@{passed=$false;oldExe=$old;newExe=$new;startedUtc=[datetime]::UtcNow.ToString('o');groups=@();rows=@();error=$null;semantics='ABBA sequential application groups, three independent processes per group/scenario; six samples per version. Internal application elapsed time includes result delivery; external UIA polling overhead also recorded; no cold-cache claim. Dense scenario retains all original match items and tests folded result wrapper creation.'}
function Save {$report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding utf8}
function Median($values){$a=@($values|Sort-Object);if(-not $a.Count){throw 'Empty sample set.'};if($a.Count%2){return [double]$a[[int]($a.Count/2)]};return ([double]$a[$a.Count/2-1]+[double]$a[$a.Count/2])/2}
$commands=@([ordered]@{name='many-small';root=(Join-Path $root 'artifacts/phase-4-corpus-standard-64/many-small');files=5000;hits=10000},[ordered]@{name='dense-folded';root=(Resolve-Path -LiteralPath $DenseCorpusDirectory).Path;files=2000;hits=120000})
try{
 $sequence=@('old','new','new','old');$source=@{}
 for($index=0;$index -lt 4;$index++){
  $version=$sequence[$index];$exe=if($version -eq 'old'){$old}else{$new}
  foreach($command in $commands){
   $text=(& (Join-Path $PSScriptRoot 'measure-ui-delivery.ps1') -ExePath $exe -CorpusDirectory $command.root -Label "g$index-$version-$($command.name)" -ExpectedFiles $command.files -ExpectedOccurrences $command.hits -Repeat 3|Out-String)
   $short=$text|ConvertFrom-Json
   if(-not $short.passed){throw 'UI measurement failed.'}
   $r=Get-Content -LiteralPath $short.reportPath -Raw|ConvertFrom-Json
   if(-not $r.passed -or -not $r.ownedProcessClosed -or $r.sourceBefore -ne $r.sourceAfter -or @($r.rounds).Count -ne 3){throw 'UI report/integrity invalid.'}
   if(-not $source.ContainsKey($command.name)){$source[$command.name]=$r.sourceBefore}elseif($source[$command.name] -cne $r.sourceBefore){throw 'UI source corpus differs across groups.'}
   if($r.exeSha256 -ne (Get-FileHash -LiteralPath $exe).Hash){throw 'UI executable drift.'}
   foreach($round in $r.rounds){if(-not $round.passed -or -not $round.ownedProcessClosed -or $round.files -ne $command.files -or $round.occurrences -ne $command.hits){throw 'UI round result differs.'}}
   $report.groups+=@([ordered]@{group=$index;version=$version;scenario=$command.name;report=$short.reportPath;exeSha256=$r.exeSha256;desktopSha256=$r.desktopSha256;coreSha256=$r.coreSha256});Save
   Write-Host "Completed g$index-$version-$($command.name)"
  }
 }
 foreach($command in $commands){foreach($version in @('old','new')){
  $samples=@(foreach($group in $report.groups|Where-Object {$_.version -eq $version -and $_.scenario -eq $command.name}){(Get-Content -LiteralPath $group.report -Raw|ConvertFrom-Json).rounds})
  if($samples.Count -ne 6){throw 'Expected six UI samples.'}
  $report.rows+=@([ordered]@{version=$version;scenario=$command.name;samples=6;appElapsedMs=Median $samples.endToEndMillisecondsRounded;externalElapsedMs=Median $samples.externalElapsedMilliseconds;cpuMs=Median $samples.searchCpuMilliseconds;peakWorkingSetBytesLifetime=Median $samples.peakWorkingSetBytesLifetime;workingSetBytes=Median $samples.workingSetBytesAtEnd;sampledPrivateBytes=Median $samples.maximumSampledPrivateBytes;appRaw=@($samples.endToEndMillisecondsRounded);externalRaw=@($samples.externalElapsedMilliseconds);cpuRaw=@($samples.searchCpuMilliseconds);peakRaw=@($samples.peakWorkingSetBytesLifetime);workingSetRaw=@($samples.workingSetBytesAtEnd);privateRaw=@($samples.maximumSampledPrivateBytes)})
 }}
 $report.passed=$true
}catch{$report.error=$_.Exception.Message;throw}finally{
 $report.finishedUtc=[datetime]::UtcNow.ToString('o');Save
 [ordered]@{passed=$report.passed;summaryPath=(Join-Path $out 'summary.json');error=$report.error}|ConvertTo-Json
}
