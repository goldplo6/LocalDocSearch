#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputRoot)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))) '..')).Path
. (Join-Path $root 'scripts/release-common.ps1')
if(-not $OutputRoot){$OutputRoot=Join-Path $root ('artifacts/concurrency-validation-'+[guid]::NewGuid().ToString('N'))}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot){throw 'Preserve existing experiment evidence; choose a new output directory.'}
New-Item -ItemType Directory -Path $OutputRoot|Out-Null
$snapshot=Join-Path $OutputRoot 'build-input'
$source=@(Copy-ReleaseSnapshot $root $snapshot)
Write-ReleaseJson (Join-Path $OutputRoot 'source-inputs.json') $source
$sourceLocks=@($source|Where-Object path -match '(^|/)packages\.lock\.json$')
$logs=Join-Path $OutputRoot 'logs';New-Item -ItemType Directory -Path $logs|Out-Null
$tests=Join-Path $OutputRoot 'test-results'
$env:DOTNET_CLI_HOME=Join-Path $root '.tools/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $root '.tools/nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$shallow=Join-Path 'C:\LocalDocSearch' 'DocSearch.exe'
$record=[ordered]@{passed=$false;kind='framework-dependent-local-experiment';defaultWorkers=1;productionDirectoryUpdated=$false;outputRoot=$OutputRoot;snapshot=$snapshot;sourceFiles=$source.Count;lockFiles=$sourceLocks.Count;tests=@();testsPassed=0;shallowExeSha256Before=$(if(Test-Path -LiteralPath $shallow){Get-ReleaseHash $shallow}else{$null});sdk=(& $dotnet --version|Out-String).Trim();startedUtc=[datetime]::UtcNow.ToString('O')}
function Invoke-Step([string]$Name,[string[]]$Arguments){
 & $dotnet @Arguments 2>&1|Tee-Object -FilePath (Join-Path $logs ($Name+'.log'))|ForEach-Object {Write-Host $_}
 if($LASTEXITCODE -ne 0){throw ('Experiment step failed: '+$Name+' ('+$LASTEXITCODE+')')}
}
Push-Location $snapshot
try{
 $common=@('-p:BuildInParallel=false','-p:UseSharedCompilation=false','-m:1')
 Invoke-Step '01-locked-restore' (@('restore','LocalDocSearch.slnx','--locked-mode','--disable-parallel')+$common)
 foreach($entry in $sourceLocks){if((Get-ReleaseHash (Join-Path $snapshot $entry.path)) -ne $entry.sha256){throw ('Snapshot lock changed: '+$entry.path)}}
 Invoke-Step '02-build' (@('build','LocalDocSearch.slnx','-c','Release','--no-restore',('-p:PathMap='+$snapshot+'=/_/src'))+$common)
 $projects=@(Get-ChildItem (Join-Path $snapshot 'tests') -Filter '*.csproj' -File -Recurse|Where-Object {[xml]$xml=Get-Content -LiteralPath $_.FullName -Raw;$flag=$xml.SelectSingleNode('//*[local-name()="IsTestProject"]');$flag -and $flag.InnerText -eq 'true'}|Sort-Object FullName)
 if($projects.Count -ne 6){throw 'Expected six regression projects.'}
 foreach($project in $projects){
  Invoke-Step ('03-test-'+$project.BaseName) (@('test',$project.FullName,'-c','Release','--no-build','--no-restore','--logger',('trx;LogFileName='+$project.BaseName+'.trx'),'--results-directory',$tests)+$common)
  [xml]$trx=Get-Content -LiteralPath (Join-Path $tests ($project.BaseName+'.trx')) -Raw
  $counters=$trx.SelectSingleNode('//*[local-name()="Counters"]')
  if(-not $counters -or [int]$counters.total -le 0 -or [int]$counters.total -ne [int]$counters.passed -or [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0){throw ('Incomplete regression: '+$project.BaseName)}
  $record.tests+=@{project=$project.BaseName;passed=[int]$counters.passed;failed=[int]$counters.failed;skipped=[int]$counters.notExecuted}
  $record.testsPassed += [int]$counters.passed
 }
 foreach($entry in $source){if((Get-ReleaseHash (Join-Path $root $entry.path)) -ne $entry.sha256 -or (Get-ReleaseHash (Join-Path $snapshot $entry.path)) -ne $entry.sha256){throw ('Source changed: '+$entry.path)}}
 $record.benchmarkDll=Join-Path $snapshot 'tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/LocalDocSearch.Benchmarks.dll'
 $record.desktopExe=Join-Path $snapshot 'src/LocalDocSearch.Desktop/bin/Release/net10.0/DocSearch.exe'
 $record.benchmarkSha256=Get-ReleaseHash $record.benchmarkDll
 $record.desktopSha256=Get-ReleaseHash $record.desktopExe
 $record.desktopDllSha256=Get-ReleaseHash (Join-Path (Split-Path $record.desktopExe) 'DocSearch.dll')
 $record.shallowExeSha256After=$(if(Test-Path -LiteralPath $shallow){Get-ReleaseHash $shallow}else{$null})
 if($record.shallowExeSha256Before -ne $record.shallowExeSha256After){throw 'Delivered executable changed during experiment build.'}
 $record.sourceUnchanged=$true
 $record.passed=$true
}finally{
 Pop-Location
 $record.finishedUtc=[datetime]::UtcNow.ToString('O')
 Write-ReleaseJson (Join-Path $OutputRoot 'build-record.json') $record
}
$record|ConvertTo-Json -Depth 8
