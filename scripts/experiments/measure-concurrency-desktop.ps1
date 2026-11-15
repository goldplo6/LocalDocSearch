#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildRecordPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=(Resolve-Path (Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))) '..')).Path
$build=Get-Content -LiteralPath $BuildRecordPath -Raw|ConvertFrom-Json
if(-not $build.passed){throw 'A successful isolated regression build is required.'}
$exe=(Resolve-Path -LiteralPath $build.desktopExe).Path
if((Get-FileHash -LiteralPath $exe).Hash -ne $build.desktopSha256){throw 'Experiment executable changed.'}
$runtime=Join-Path $root '.tools/dotnet'
$pwsh=Join-Path $PSHOME 'pwsh.exe'
$output=Join-Path $root ('artifacts/concurrency-desktop-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
$corpus=Join-Path $root 'artifacts/phase-4-corpus-standard-64/many-small'
function Source-Sha {
 $state=@(Get-ChildItem -LiteralPath $corpus -File -Recurse|Sort-Object FullName|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($corpus,$_.FullName);length=$_.Length;ticks=$_.LastWriteTimeUtc.Ticks;sha=(Get-FileHash -LiteralPath $_.FullName).Hash}})|ConvertTo-Json -Compress -Depth 5
 [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($state)))
}
function Distribution($Values){$s=@($Values|ForEach-Object {[double]$_}|Sort-Object);if(-not $s.Count){throw 'Missing desktop samples.'};$m=[int][Math]::Floor($s.Count/2);$median=if($s.Count%2){$s[$m]}else{($s[$m-1]+$s[$m])/2};[ordered]@{count=$s.Count;minimum=$s[0];median=$median;maximum=$s[-1]}}
function Child([string]$Name,[string]$Script,[string[]]$Arguments){
 $text=(& $pwsh -NoProfile -File (Join-Path $root $Script) @Arguments|Out-String)
 $exit=$LASTEXITCODE
 $text|Set-Content -LiteralPath (Join-Path $output ($Name+'.stdout.log')) -Encoding utf8
 if($exit -ne 0){throw ('Desktop step failed: '+$Name)}
 $result=$text|ConvertFrom-Json
 if(-not $result.passed -or -not $result.ownedProcessClosed){throw ('Desktop evidence failed: '+$Name)}
 $result
}
$sourceBefore=Source-Sha
$controls=[Collections.Generic.List[object]]::new()
$groups=[Collections.Generic.List[object]]::new()
$samples=[Collections.Generic.List[object]]::new()
$record=[ordered]@{passed=$false;exe=$exe;exeSha256=$build.desktopSha256;desktopDllSha256=$build.desktopDllSha256;buildRecordPath=(Resolve-Path -LiteralPath $BuildRecordPath).Path;sourceSha256Before=$sourceBefore;workersSequence=@(1,2,4,4,2,1);roundsPerGroup=3;controls=@();groups=@();measurements=@();sourceUnchanged=$false;deliveredExeUnchanged=$false;clipboardTouched=$false;globalInputUsed=$false;notes='Fresh owned application process per UI round; internal duration includes queue drain and sorting; external duration includes UIA polling; OS cache not cleared; working set is lifetime peak, private bytes sampled every 25ms; UIA uses the existing WindowsDesktop 6 tooling only.'}
try{
 foreach($w in @(1,2,4)){
  Write-Host ('Native controls, workers='+$w)
  $item=Child ('controls-w'+$w) 'scripts/experiments/verify-concurrency-controls-uia.ps1' @('-ExePath',$exe,'-Workers',[string]$w,'-RuntimeRoot',$runtime)
  $detail=Get-Content -LiteralPath $item.reportPath -Raw|ConvertFrom-Json
  if($detail.workers -ne $w -or $detail.exeSha256 -ne $build.desktopSha256 -or -not $detail.sourceFilesUnchanged -or @($detail.scenarios).Count -ne 4 -or @($detail.scenarios|Where-Object {-not $_.passed}).Count){throw 'Native control count or identity mismatch.'}
  $controls.Add([ordered]@{workers=$w;reportPath=$item.reportPath;reportSha256=(Get-FileHash -LiteralPath $item.reportPath).Hash;passed=$true})
 }
 $sequence=@(1,2,4,4,2,1)
 for($g=0;$g -lt $sequence.Count;$g++){
  $w=$sequence[$g]
  Write-Host ('UI timing group='+$g+', workers='+$w)
  if((Source-Sha) -ne $sourceBefore){throw 'Corpus changed before UI group.'}
  $item=Child ('ui-g'+$g+'-w'+$w) 'scripts/experiments/measure-concurrency-ui.ps1' @('-ExePath',$exe,'-Label',('g'+$g+'-w'+$w),'-Repeat','3','-Workers',[string]$w,'-RuntimeRoot',$runtime)
  $detail=Get-Content -LiteralPath $item.reportPath -Raw|ConvertFrom-Json
  if($detail.workers -ne $w -or $detail.exeSha256 -ne $build.desktopSha256 -or @($detail.rounds).Count -ne 3){throw 'UI group count or identity mismatch.'}
  foreach($row in $detail.rounds){if(-not $row.passed -or -not $row.ownedProcessClosed -or $row.files -ne 5000 -or $row.occurrences -ne 10000){throw 'UI round did not complete the original corpus.'};$samples.Add([pscustomobject]@{workers=$w;group=$g;round=$row})}
  if((Source-Sha) -ne $sourceBefore){throw 'Corpus changed after UI group.'}
  $groups.Add([ordered]@{group=$g;workers=$w;reportPath=$item.reportPath;reportSha256=(Get-FileHash -LiteralPath $item.reportPath).Hash})
 }
 $record.measurements=@(foreach($w in @(1,2,4)){$rows=@($samples|Where-Object workers -eq $w|ForEach-Object round);if($rows.Count -ne 6){throw 'Expected six independent application runs per worker mode.'};[ordered]@{workers=$w;internalMilliseconds=Distribution ($rows|ForEach-Object endToEndMillisecondsRounded);externalMilliseconds=Distribution ($rows|ForEach-Object externalElapsedMilliseconds);lifetimePeakWorkingSetBytes=Distribution ($rows|ForEach-Object peakWorkingSetBytesLifetime);maximumSampledPrivateBytes=Distribution ($rows|ForEach-Object maximumSampledPrivateBytes);processTotalCpuMilliseconds=Distribution ($rows|ForEach-Object totalProcessorMilliseconds)}})
 $record.sourceSha256After=Source-Sha
 $record.sourceUnchanged=$record.sourceSha256After -eq $sourceBefore
 $shallow=Join-Path 'C:\LocalDocSearch' 'DocSearch.exe'
 $record.deliveredExeUnchanged=(Get-FileHash -LiteralPath $shallow).Hash -eq $build.shallowExeSha256Before
 if(-not $record.sourceUnchanged -or -not $record.deliveredExeUnchanged -or (Get-FileHash -LiteralPath $exe).Hash -ne $build.desktopSha256 -or (Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'DocSearch.dll')).Hash -ne $build.desktopDllSha256){throw 'Final desktop identity/integrity check failed.'}
 $record.passed=$true
}catch{$record.error=$_.Exception.Message;throw}finally{
 $record.controls=$controls.ToArray();$record.groups=$groups.ToArray();$record.finishedUtc=[datetime]::UtcNow.ToString('O')
 $record.reportPath=Join-Path $output 'summary.json'
 $record|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $record.reportPath -Encoding utf8
}
$record|ConvertTo-Json -Depth 12
