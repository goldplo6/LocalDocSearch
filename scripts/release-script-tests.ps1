#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputRoot, [string]$TrxDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$testRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $testRoot 'artifacts/release-script-tests' }
$run = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
foreach ($script in @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Name -match '(release|license).*\.ps1$' })) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ('PowerShell parse failed in ' + $script.Name + ': ' + (($errors | ForEach-Object Message) -join '; ')) }
}
$checks.Add('All release/license PowerShell scripts parse successfully')
$assetsPath = Join-Path $testRoot 'src/LocalDocSearch.Desktop/obj/project.assets.json'
if (-not (Test-Path $assetsPath)) { throw 'Restore locked source dependencies first (scripts/dev.ps1 -Action Restore -Locked); script tests need actual installed package evidence.' }
& (Join-Path $PSScriptRoot 'license-inventory.ps1') -OutputDirectory (Join-Path $run 'actual-inventory') | Out-Host
$actual = Get-Content (Join-Path $run 'actual-inventory/inventory.json') -Raw | ConvertFrom-Json -AsHashtable
if (-not $actual.readyForCandidate -or $actual.packageCount -lt 1 -or @($actual.packages | Where-Object { -not $_.nugetContentHashVerified }).Count) { throw 'Actual dependency evidence validation failed.' }
$checks.Add('Actual Desktop graph: nupkg canonical hashes and preserved license/NOTICE bytes verified')
$snapshot = Join-Path $run 'snapshot'
$source = @(Copy-ReleaseSnapshot $testRoot $snapshot)
if (@($source | Where-Object path -Match '(^|/)(bin|obj|\.tools|\.git)(/|$)|(^|/)settings\.json$').Count) { throw 'Snapshot whitelist leaked excluded input.' }
foreach ($record in $source) { if ((Get-ReleaseHash (Join-Path $snapshot $record.path)) -ne $record.sha256) { throw 'Snapshot byte verification failed.' } }
$checks.Add('Real whitelisted source snapshot preserves recorded bytes and excludes runtime/user/build files')
$zipInput = Join-Path $run 'zip-input'
New-Item -ItemType Directory -Path (Join-Path $zipInput 'nested') -Force | Out-Null
Copy-Item (Join-Path $testRoot 'LICENSE') (Join-Path $zipInput 'LICENSE')
Copy-Item (Join-Path $testRoot 'THIRD-PARTY-NOTICES.md') (Join-Path $zipInput 'nested/THIRD-PARTY-NOTICES.md')
$firstZip = Join-Path $run 'first.zip'; $secondZip = Join-Path $run 'second.zip'
New-ReleaseZip $zipInput $firstZip 'license-evidence'
New-ReleaseZip $zipInput $secondZip 'license-evidence'
if ((Get-ReleaseHash $firstZip) -ne (Get-ReleaseHash $secondZip)) { throw 'Stable ZIP helper produced different bytes for identical document inputs.' }
$archive = [IO.Compression.ZipFile]::OpenRead($firstZip)
try {
    foreach ($entry in $archive.Entries) {
        $sourcePath = Join-Path $zipInput $entry.FullName.Substring('license-evidence/'.Length)
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($hash -ne (Get-ReleaseHash $sourcePath) -or $entry.LastWriteTime.Year -ne 2026) { throw 'ZIP content/timestamp verification failed.' }
    }
} finally { $archive.Dispose() }
$checks.Add('Two ZIPs of actual license documents match byte-for-byte with verified contents and fixed timestamps')
# Modify an isolated copy of a real dependency to exercise unknown-license and cache-tamper blocking.
$originalAssets = Get-Content $assetsPath -Raw | ConvertFrom-Json -AsHashtable
$key = 'Tmds.DBus.Protocol/0.94.1'
if (-not $originalAssets.libraries.Contains($key)) { throw 'Real negative-license fixture package changed; review the script test.' }
$isolatedPackages = Join-Path $run 'tampered-packages'
$packageRelative = $originalAssets.libraries[$key].path
$packageDestination = Join-Path $isolatedPackages $packageRelative
New-Item -ItemType Directory -Path $packageDestination -Force | Out-Null
Copy-Item (Join-Path $testRoot ('.tools/nuget/packages/' + $packageRelative + '/*')) $packageDestination -Recurse
$specPath = (Get-ChildItem $packageDestination -Filter '*.nuspec' -File).FullName
[xml]$spec = Get-Content $specPath -Raw
$spec.SelectSingleNode('//*[local-name()="license"]').InnerText = 'UNREVIEWED-TEST-LICENSE'
$spec.Save($specPath)
$isolatedAssets = @{ targets=$originalAssets.targets; libraries=@{$key=$originalAssets.libraries[$key]} }
$isolatedAssetsPath = Join-Path $run 'tampered.assets.json'; Write-ReleaseJson $isolatedAssetsPath $isolatedAssets
$blocked = $false
try { & (Join-Path $PSScriptRoot 'license-inventory.ps1') -AssetsPath $isolatedAssetsPath -PackageRoot $isolatedPackages -OutputDirectory (Join-Path $run 'blocked-inventory') | Out-Null }
catch { if ($_.Exception.Message -like '*unreviewed license expression*' -and $_.Exception.Message -like '*extracted evidence differs*') { $blocked=$true } else { throw } }
if (-not $blocked) { throw 'Unknown license/cache tamper did not block candidate evidence.' }
$checks.Add('Unknown license and tampered extracted nuspec fail closed on an isolated real-package copy')
$existingRoot = Join-Path $run 'existing-output'
$existingCandidate = Join-Path $existingRoot 'LocalDocSearch-1.0.0-rc.1-win-x64'
New-Item -ItemType Directory -Path $existingCandidate -Force | Out-Null
$blocked = $false
try { & (Join-Path $PSScriptRoot 'release.ps1') -OutputRoot $existingRoot | Out-Null }
catch { if ($_.Exception.Message -like '*Candidate directory already exists*') { $blocked=$true } else { throw } }
if (-not $blocked) { throw 'Existing output did not fail closed.' }
$checks.Add('Existing candidate output is rejected before any restore/build/test/publish')
if ($TrxDirectory) {
    $realRecords = [Collections.Generic.List[object]]::new()
    $expectedPassed = 0
    $trxEvidence = @(Get-ChildItem -LiteralPath $TrxDirectory -File -Filter '*.trx' | Sort-Object Name)
    if ($trxEvidence.Count -eq 0) { throw 'No actual TRX evidence found for test-summary regression.' }
    foreach ($trxFile in $trxEvidence) {
        [xml]$trxXml = Get-Content $trxFile.FullName -Raw
        $counters = $trxXml.SelectSingleNode('//*[local-name()="Counters"]')
        if (-not $counters -or [int]$counters.passed -lt 1 -or [int]$counters.total -ne [int]$counters.passed) { throw 'TRX regression requires completed successful real tests.' }
        $expectedPassed += [int]$counters.total
        $realRecords.Add([ordered]@{project=$trxFile.BaseName; total=[int]$counters.total; passed=[int]$counters.passed; failed=[int]$counters.failed; skipped=[int]$counters.notExecuted})
    }
    $summary = Get-ReleaseTestSummary $realRecords
    $summaryPath = Join-Path $run 'actual-trx-summary.json'
    Write-ReleaseJson $summaryPath $summary
    $roundTrip = Get-Content $summaryPath -Raw | ConvertFrom-Json -AsHashtable
    if ($summary.tests -isnot [array] -or $roundTrip.tests.Count -ne $trxEvidence.Count -or $roundTrip.testsPassed -ne $expectedPassed) { throw 'Ordered-dictionary count or generic-list serialization regression.' }
    $singleRecord = [Collections.Generic.List[object]]::new()
    $singleRecord.Add($realRecords[0])
    $singleSummary = Get-ReleaseTestSummary $singleRecord
    if ($singleSummary.tests -isnot [array] -or $singleSummary.tests.Count -ne 1 -or $singleSummary.testsPassed -ne $realRecords[0]['passed']) { throw 'Single-project test summary lost array shape/count.' }
    $checks.Add(('Actual TRX ordered records round-trip as arrays with exact numeric sum: {0} projects, {1} passed' -f $trxEvidence.Count,$expectedPassed))
}
Write-ReleaseJson (Join-Path $run 'results.json') ([ordered]@{passed=$checks.Count; checks=@($checks); scope='release-script checks only; no application compilation, launch or certification'})
[pscustomobject]@{passed=$checks.Count; results=Get-ReleaseRelativePath $testRoot (Join-Path $run 'results.json')}
