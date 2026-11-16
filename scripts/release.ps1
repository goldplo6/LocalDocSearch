#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z][0-9A-Za-z.-]*$')]
    [string]$Version = '1.0.0-rc.1',
    [string]$OutputRoot,
    [switch]$IncludeSamples
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$releaseRoot = Split-Path $PSScriptRoot -Parent
if (-not $IsWindows) { throw 'This candidate builder requires Windows and PowerShell 7.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $releaseRoot 'artifacts/releases' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$name = 'LocalDocSearch-' + $Version + '-win-x64'
$candidate = Join-Path $OutputRoot $name
if (Test-Path -LiteralPath $candidate) { throw 'Candidate directory already exists. Preserve its evidence and choose a new version or output root.' }
$dotnet = Join-Path $releaseRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'Install the pinned local SDK with scripts/bootstrap-sdk.ps1 first.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $releaseRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $releaseRoot '.tools/nuget/packages'
$expectedSdk = (Get-Content (Join-Path $releaseRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
Push-Location $releaseRoot
try { $actualSdk = (& $dotnet --version | Out-String).Trim(); if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) { throw "Expected SDK $expectedSdk; resolved $actualSdk" } } finally { Pop-Location }
$sdkDirectory = Join-Path $releaseRoot ('.tools/dotnet/sdk/' + $actualSdk)
[xml]$bundledVersions = Get-Content (Join-Path $sdkDirectory 'Microsoft.NETCoreSdk.BundledVersions.props') -Raw
$runtimeReference = @($bundledVersions.SelectNodes('//*[local-name()="KnownFrameworkReference" and @Include="Microsoft.NETCore.App" and @TargetFramework="net10.0"]'))
if ($runtimeReference.Count -ne 1) { throw 'Expected one net10.0 runtime pin from the selected SDK.' }
$runtimeVersion = [string]$runtimeReference[0].LatestRuntimeFrameworkVersion
New-Item -ItemType Directory -Path $candidate | Out-Null
$logs = Join-Path $candidate 'logs'; New-Item -ItemType Directory -Path $logs | Out-Null
$snapshot = Join-Path $candidate 'build-input'
$package = Join-Path $candidate $name
$sourceRecords = @(Copy-ReleaseSnapshot $releaseRoot $snapshot)
Write-ReleaseJson (Join-Path $candidate 'source-inputs.json') $sourceRecords
$sourceLocks = @($sourceRecords | Where-Object path -Match '(^|/)packages\.lock\.json$')
function Invoke-ReleaseDotnet([string]$Step, [string[]]$Arguments) {
    Write-Host ('[' + $Step + '] dotnet ' + ($Arguments -join ' '))
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs ($Step + '.log')) | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Release step '$Step' failed with exit code $LASTEXITCODE. Candidate remains incomplete; inspect logs and use a new output root." }
}
$common = @('-p:BuildInParallel=false','-p:UseSharedCompilation=false','-m:1')
$pathMap = '-p:PathMap=' + $snapshot + '=/_/src'
$results = Join-Path $candidate 'test-results'
$testRecords = [Collections.Generic.List[object]]::new()
Push-Location $snapshot
try {
    # Validate byte-identical copies of the existing source lock files first.
    Invoke-ReleaseDotnet '01-source-locked-restore' (@('restore','LocalDocSearch.slnx','--locked-mode','--disable-parallel') + $common)
    foreach ($sourceLock in $sourceLocks) { if ((Get-ReleaseHash (Join-Path $snapshot $sourceLock.path)) -ne $sourceLock.sha256) { throw "Source lock copy changed during locked validation: $($sourceLock.path)" } }
    Invoke-ReleaseDotnet '02-source-build' (@('build','LocalDocSearch.slnx','-c','Release','--no-restore',$pathMap) + $common)
    $testProjects = @(Get-ChildItem (Join-Path $snapshot 'tests') -Filter '*.csproj' -File -Recurse | Where-Object { [xml]$project = Get-Content $_.FullName -Raw; $testFlag = $project.SelectSingleNode('//*[local-name()="IsTestProject"]'); $testFlag -and $testFlag.InnerText -eq 'true' } | Sort-Object FullName)
    if ($testProjects.Count -eq 0) { throw 'No test projects found in source snapshot.' }
    foreach ($testProject in $testProjects) {
        $testName = $testProject.BaseName
        Invoke-ReleaseDotnet ('03-test-' + $testName) (@('test', $testProject.FullName,'-c','Release','--no-build','--no-restore','--logger',('trx;LogFileName=' + $testName + '.trx'),'--results-directory',$results) + $common)
        $trx = Join-Path $results ($testName + '.trx')
        if (-not (Test-Path $trx)) { throw "TRX evidence missing: $testName" }
        [xml]$testXml = Get-Content $trx -Raw
        $counters = $testXml.SelectSingleNode('//*[local-name()="Counters"]')
        if (-not $counters -or [int]$counters.executed -eq 0 -or [int]$counters.failed -gt 0 -or [int]$counters.notExecuted -gt 0) { throw "Incomplete or failing tests: $testName" }
        $testRecords.Add([ordered]@{project=Get-ReleaseRelativePath $snapshot $testProject.FullName; total=[int]$counters.total; passed=[int]$counters.passed; failed=[int]$counters.failed; skipped=[int]$counters.notExecuted})
    }
    $desktopProject = 'src/LocalDocSearch.Desktop/LocalDocSearch.Desktop.csproj'
    $desktopAssets = Join-Path $snapshot 'src/LocalDocSearch.Desktop/obj/project.assets.json'
    $baselineAssets = Get-Content $desktopAssets -Raw | ConvertFrom-Json -AsHashtable
    $baselinePackages = @($baselineAssets.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object Key | Sort-Object)
    $ridProperties = @('-p:SelfContained=true','-p:RuntimeIdentifier=win-x64','-p:UseAppHost=true',('-p:RuntimeFrameworkVersion=' + $runtimeVersion),'-p:PublishTrimmed=false','-p:PublishAot=false','-p:PublishSingleFile=false')
    # RID locks are deliberately generated only inside this snapshot, then frozen and revalidated.
    Invoke-ReleaseDotnet '04-candidate-rid-resolve' (@('restore',$desktopProject,'--runtime','win-x64','--force-evaluate','--disable-parallel') + $ridProperties + $common)
    $candidateLocks = @(Get-ChildItem (Join-Path $snapshot 'src') -Filter packages.lock.json -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{path=Get-ReleaseRelativePath $snapshot $_.FullName; sha256=Get-ReleaseHash $_.FullName} })
    Invoke-ReleaseDotnet '05-candidate-rid-locked-restore' (@('restore',$desktopProject,'--runtime','win-x64','--locked-mode','--disable-parallel') + $ridProperties + $common)
    foreach ($candidateLock in $candidateLocks) { if ((Get-ReleaseHash (Join-Path $snapshot $candidateLock.path)) -ne $candidateLock.sha256) { throw "Candidate RID lock drift: $($candidateLock.path)" } }
    $ridAssets = Get-Content $desktopAssets -Raw | ConvertFrom-Json -AsHashtable
    $ridPackages = @($ridAssets.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object Key | Sort-Object)
    $graphDrift = @(Compare-Object $baselinePackages $ridPackages)
    if ($graphDrift.Count) { throw ('RID restore changed the reviewed NuGet graph: ' + (($graphDrift | ForEach-Object InputObject) -join ', ')) }
    Invoke-ReleaseDotnet '06-publish' (@('publish',$desktopProject,'-c','Release','--runtime','win-x64','--self-contained','true','--no-restore','--output',$package,('-p:Version=' + $Version),('-p:InformationalVersion=' + $Version),'-p:IncludeSourceRevisionInInformationalVersion=false','-p:ContinuousIntegrationBuild=true',$pathMap) + $ridProperties + $common)
} finally { Pop-Location }
# Check source locks stayed intact even though all restores/builds ran in isolation.
foreach ($sourceLock in $sourceLocks) { if ((Get-ReleaseHash (Join-Path $releaseRoot $sourceLock.path)) -ne $sourceLock.sha256) { throw "Source lock changed externally during release: $($sourceLock.path)" } }
foreach ($document in @('LICENSE','THIRD-PARTY-NOTICES.md','README.md')) { Copy-Item -LiteralPath (Join-Path $releaseRoot $document) -Destination (Join-Path $package $document) }
New-Item -ItemType Directory -Path (Join-Path $package 'docs') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $package 'skills/docsearch') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $releaseRoot 'skills/docsearch/SKILL.md') -Destination (Join-Path $package 'skills/docsearch/SKILL.md')
Copy-Item (Join-Path $releaseRoot 'docs/dependency-licenses.sources.json') (Join-Path $package 'docs/dependency-licenses.sources.json')
$licenseDirectory = Join-Path $package 'docs/dependency-licenses'
& (Join-Path $PSScriptRoot 'license-inventory.ps1') -AssetsPath $desktopAssets -PackageRoot $env:NUGET_PACKAGES -SdkDirectory $sdkDirectory -OutputDirectory $licenseDirectory -PublishedDirectory $package -RuntimeVersion $runtimeVersion | Out-Host
if ($IncludeSamples) {
    foreach ($fixture in @('mixed','long-documents')) {
        $fixtureDestination = Join-Path $package ('samples/' + $fixture)
        New-Item -ItemType Directory -Path $fixtureDestination -Force | Out-Null
        foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $snapshot ('tests/fixtures/' + $fixture)) -File)) { Copy-Item -LiteralPath $file.FullName -Destination $fixtureDestination }
    }
}
$publishedRuntime = Join-Path $package 'coreclr.dll'
$runtimePackCore = Join-Path $env:NUGET_PACKAGES ('microsoft.netcore.app.runtime.win-x64/' + $runtimeVersion + '/runtimes/win-x64/native/coreclr.dll')
if ((Get-ReleaseHash $publishedRuntime) -ne (Get-ReleaseHash $runtimePackCore)) { throw 'Published coreclr.dll does not match the recorded runtime pack.' }
$testSummary = Get-ReleaseTestSummary $testRecords
$record = [ordered]@{
    schemaVersion=1; product='LocalDocSearch'; version=$Version; status='local-release-candidate'; rid='win-x64'
    sdk=$actualSdk; runtime=$runtimeVersion; selfContained=$true; trimmed=$false; nativeAot=$false; singleFile=$false; upx=$false
    sourceSnapshot='whitelisted, byte-verified source copy; no user settings, SDK/cache, obj or bin inputs'
    pathMap='snapshot root => /_/src'; sourceInputs=$sourceRecords; sourceLocks=$sourceLocks; candidateRidLocks=$candidateLocks
    lockScope='Source lock copies validated before RID resolve. Candidate-only RID locks generated in isolated snapshot then --locked-mode revalidated; not an already-committed frozen RID graph.'
    reproducibility='Locked versions, recorded package/runtime hashes, deterministic build inputs and stable ZIP entry order/timestamps. Byte-identical independent clean builds have not been demonstrated.'
    tests=$testSummary.tests; testsPassed=$testSummary.testsPassed; samplesIncluded=[bool]$IncludeSamples
    dependencyInventory='docs/dependency-licenses/inventory.json'; dependencyInventorySha256=Get-ReleaseHash (Join-Path $licenseDirectory 'inventory.json')
    releaseValidation='Package structure, self-contained runtime, checksum and license-evidence checks; no clean-machine, 360, certificate reputation or code-signing certification.'
    codeSigning='unsigned'; zipEntryTimestampUtc='2026-01-01T00:00:00Z'
}
Write-ReleaseJson (Join-Path $package 'build-record.json') $record
$validation = Assert-ReleasePackage $package
$manifest = @(Get-ReleaseFiles $package | ForEach-Object { [ordered]@{path=Get-ReleaseRelativePath $package $_; bytes=(Get-Item -LiteralPath $_).Length; sha256=Get-ReleaseHash $_} })
Write-ReleaseJson (Join-Path $package 'file-manifest.json') $manifest
$zip = Join-Path $candidate ($name + '.zip')
New-ReleaseZip $package $zip $name
$zipHash = Get-ReleaseHash $zip
($zipHash + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content (Join-Path $candidate 'SHA256SUMS.txt') -Encoding ascii
& (Join-Path $PSScriptRoot 'release-verify.ps1') -CandidateDirectory $candidate
Write-ReleaseJson (Join-Path $candidate 'validation.json') $validation
Write-Host ('Candidate ready: ' + $zip)
Write-Host ('SHA256: ' + $zipHash)
