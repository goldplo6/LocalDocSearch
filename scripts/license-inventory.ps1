#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$AssetsPath,
    [string]$PackageRoot,
    [string]$SdkDirectory,
    [string]$OutputDirectory,
    [string]$PublishedDirectory,
    [string]$RuntimeVersion,
    [switch]$Refresh
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$licenseRoot = Split-Path $PSScriptRoot -Parent
if (-not $AssetsPath) { $AssetsPath = Join-Path $licenseRoot 'src/LocalDocSearch.Desktop/obj/project.assets.json' }
if (-not $PackageRoot) { $PackageRoot = Join-Path $licenseRoot '.tools/nuget/packages' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $licenseRoot 'docs/dependency-licenses' }
$AssetsPath = [IO.Path]::GetFullPath($AssetsPath)
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
function Get-LicenseHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-LicenseJson([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
function Copy-LicenseEvidence([string]$Source, [string]$RelativePath, [string]$Origin, [string]$Url = '') {
    $destination = Join-Path $OutputDirectory $RelativePath
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    return [ordered]@{ path = $RelativePath.Replace('\','/'); sha256 = Get-LicenseHash $destination; origin = $Origin; url = $Url }
}
if ((Test-Path (Join-Path $OutputDirectory 'inventory.json')) -and -not $Refresh) { throw 'License inventory already exists; inspect it and pass -Refresh for an intentional regeneration.' }
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
$sources = @(Get-Content (Join-Path $licenseRoot 'docs/dependency-licenses.sources.json') -Raw | ConvertFrom-Json -AsHashtable)
if (-not $SdkDirectory) {
    $pinnedSdk = (Get-Content (Join-Path $licenseRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $SdkDirectory = Join-Path $licenseRoot ('.tools/dotnet/sdk/' + $pinnedSdk)
}
# NuGet's canonical content hash excludes signing-related archive differences.
# A raw SHA512 of the signed ZIP differs from packages.lock.json contentHash.
foreach ($assembly in @('NuGet.Common','NuGet.Versioning','NuGet.Frameworks','NuGet.Packaging')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $SdkDirectory ($assembly + '.dll')))
}
$blockers = [Collections.Generic.List[string]]::new()
$inventory = [Collections.Generic.List[object]]::new()
$publishedNames = @()
if ($PublishedDirectory) { $publishedNames = @(Get-ChildItem -LiteralPath $PublishedDirectory -Recurse -File | ForEach-Object Name) }
$packageLibraries = @($assets.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' } | Sort-Object Key)
foreach ($library in $packageLibraries) {
    $id, $version = $library.Key.Split('/', 2)
    $packageDirectory = Join-Path $PackageRoot $library.Value.path
    $prefix = 'evidence/' + $library.Value.path
    $nuspec = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File)
    if ($nuspec.Count -ne 1) { $blockers.Add("$($library.Key): expected one installed nuspec"); continue }
    [xml]$spec = Get-Content -LiteralPath $nuspec[0].FullName -Raw
    $metadata = $spec.SelectSingleNode('//*[local-name()="metadata"]')
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $repoUrl = if ($repository) { $repository.GetAttribute('url') } else { '' }
    $commit = if ($repository) { $repository.GetAttribute('commit') } else { '' }
    $licenseType = if ($license) { $license.GetAttribute('type') } else { 'missing' }
    $licenseValue = if ($license) { $license.InnerText } else { '' }
    $evidence = [Collections.Generic.List[object]]::new()
    $evidence.Add((Copy-LicenseEvidence $nuspec[0].FullName ($prefix + '/metadata.nuspec') 'installed-nupkg'))
    $licenseFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object { $_.Name -match '(?i)^(licen[cs]e|notice|third.?party|copying|copyright)([._-].*)?$' })
    foreach ($file in $licenseFiles) {
        $relative = [IO.Path]::GetRelativePath($packageDirectory, $file.FullName).Replace('\','/')
        $evidence.Add((Copy-LicenseEvidence $file.FullName ($prefix + '/' + $relative) 'installed-nupkg'))
    }
    # Repository evidence is accepted only at the exact commit declared by this package.
    $family = switch -Regex ($id) {
        '^Avalonia\.BuildServices$' { 'avalonia.buildservices'; break }
        '^Avalonia(?:\.|$)' { 'avalonia'; break }
        '^DocumentFormat\.OpenXml' { 'openxml'; break }
        '^MicroCom\.Runtime$' { 'microcom'; break }
        '^PdfPig$' { 'pdfpig'; break }
        '^System\.IO\.Packaging$' { 'system.io.packaging'; break }
        '^Tmds\.DBus\.Protocol$' { 'tmds'; break }
        default { '' }
    }
    foreach ($source in @($sources | Where-Object { $_.family -eq $family -and $_.commit -eq $commit -and $repoUrl.TrimEnd('/').Replace('.git','') -eq ('https://github.com/' + $_.repository) })) {
        $sourcePath = Join-Path $licenseRoot $source.path
        if (-not (Test-Path -LiteralPath $sourcePath) -or (Get-LicenseHash $sourcePath) -ne $source.sha256) {
            $blockers.Add("$($library.Key): pinned upstream evidence missing or SHA256 mismatch: $($source.path)")
            continue
        }
        $evidence.Add((Copy-LicenseEvidence $sourcePath ($prefix + '/upstream-' + [IO.Path]::GetFileName($sourcePath)) 'pinned-repository-commit' $source.url))
    }
    $hasLicenseText = @($evidence | Where-Object { [IO.Path]::GetFileName($_.path) -match '(?i)(licen[cs]e|copying)' }).Count -gt 0
    if ($licenseType -eq 'expression' -and $licenseValue -notin @('MIT','Apache-2.0')) { $blockers.Add("$($library.Key): unreviewed license expression '$licenseValue'") }
    elseif ($licenseType -eq 'file') {
        $declaredFile = Join-Path $packageDirectory $licenseValue
        if (-not (Test-Path -LiteralPath $declaredFile -PathType Leaf)) { $blockers.Add("$($library.Key): declared license file is missing") }
        # The reviewed package carries ANGLE's BSD text. Do not relabel it as MIT.
        if ($id -ne 'Avalonia.Angle.Windows.Natives' -or $version -ne '2.1.27548.20260419') { $blockers.Add("$($library.Key): unreviewed file-based license") }
    }
    elseif ($licenseType -ne 'expression') { $blockers.Add("$($library.Key): missing recognized nuspec license metadata") }
    if (-not $hasLicenseText) { $blockers.Add("$($library.Key): no preserved full license text at the declared repository commit or in the package") }
    $nupkg = Join-Path $packageDirectory ($id.ToLowerInvariant() + '.' + $version.ToLowerInvariant() + '.nupkg')
    $nupkgSha256 = ''; $verifiedSha512 = $false
    if (Test-Path -LiteralPath $nupkg -PathType Leaf) {
        $nupkgSha256 = Get-LicenseHash $nupkg
        $reader = [NuGet.Packaging.PackageArchiveReader]::new($nupkg)
        try { $actualContentHash = $reader.GetContentHash([Threading.CancellationToken]::None, $null) } finally { $reader.Dispose() }
        $verifiedSha512 = $actualContentHash -ceq $library.Value.sha512
        if (-not $verifiedSha512) { $blockers.Add("$($library.Key): NuGet canonical content hash differs from project.assets.json") }
        $archive = [IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            foreach ($item in @($evidence | Where-Object origin -EQ 'installed-nupkg')) {
                $entryName = $item.path.Substring($prefix.Length + 1)
                if ($entryName -eq 'metadata.nuspec') { $entryName = $nuspec[0].Name }
                $entry = @($archive.Entries | Where-Object { $_.FullName -ieq $entryName }) | Select-Object -First 1
                if (-not $entry) { $blockers.Add("$($library.Key): preserved evidence missing from original nupkg: $entryName"); continue }
                $entryStream = $entry.Open()
                try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant() } finally { $entryStream.Dispose() }
                if ($entryHash -cne $item.sha256) { $blockers.Add("$($library.Key): extracted evidence differs from original nupkg: $entryName") }
            }
        } finally { $archive.Dispose() }
    } else { $blockers.Add("$($library.Key): original nupkg archive missing; package checksum evidence incomplete") }
    $targetAssets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($target in $assets.targets.Values) {
        if (-not $target.Contains($library.Key)) { continue }
        $targetPackage = $target[$library.Key]
        foreach ($kind in @('runtime','native','runtimeTargets')) {
            if ($targetPackage.Contains($kind)) {
                foreach ($asset in $targetPackage[$kind].Keys) {
                    if ($PublishedDirectory -and [IO.Path]::GetFileName($asset) -in $publishedNames) { [void]$targetAssets.Add($asset) }
                }
            }
        }
    }
    $inventory.Add([ordered]@{
        id = $id; version = $version; licenseType = $licenseType; license = $licenseValue
        copyright = if ($copyright) { $copyright.InnerText } else { '' }
        repository = $repoUrl; repositoryCommit = $commit; nugetContentHashSha512 = $library.Value.sha512
        nupkgSha256 = $nupkgSha256; nugetContentHashVerified = $verifiedSha512
        publishedAssetCandidates = @($targetAssets | Sort-Object); evidence = @($evidence)
    })
}
$runtime = $null
if ($RuntimeVersion) {
    $runtimeDirectory = Join-Path $PackageRoot ('microsoft.netcore.app.runtime.win-x64/' + $RuntimeVersion)
    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeDirectory -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '(?i)licen[cs]e|third.?party|notice' })
    $runtimeEvidence = @($runtimeFiles | ForEach-Object { Copy-LicenseEvidence $_.FullName ('runtime/dotnet-' + $RuntimeVersion + '/' + $_.Name) 'installed-runtime-pack' })
    if (@($runtimeFiles | Where-Object Name -Match '(?i)license').Count -eq 0 -or @($runtimeFiles | Where-Object Name -Match '(?i)notice').Count -eq 0) { $blockers.Add(".NET runtime ${RuntimeVersion}: LICENSE and THIRD-PARTY-NOTICES are both required from the installed win-x64 runtime pack") }
    $runtimeSpec = @(Get-ChildItem -LiteralPath $runtimeDirectory -Filter '*.nuspec' -File -ErrorAction SilentlyContinue)
    $runtimeLicense = ''
    if ($runtimeSpec.Count -ne 1) { $blockers.Add(".NET runtime ${RuntimeVersion}: expected one nuspec") }
    else {
        [xml]$runtimeSpecXml = Get-Content $runtimeSpec[0].FullName -Raw
        $runtimeLicenseNode = $runtimeSpecXml.SelectSingleNode('//*[local-name()="license"]')
        if ($runtimeLicenseNode) { $runtimeLicense = $runtimeLicenseNode.InnerText }
        if (-not $runtimeLicenseNode -or $runtimeLicenseNode.GetAttribute('type') -ne 'expression' -or $runtimeLicense -ne 'MIT') { $blockers.Add(".NET runtime ${RuntimeVersion}: unreviewed runtime license metadata") }
        $runtimeEvidence += Copy-LicenseEvidence $runtimeSpec[0].FullName ('runtime/dotnet-' + $RuntimeVersion + '/' + $runtimeSpec[0].Name) 'installed-runtime-pack'
    }
    $runtimeArchive = Join-Path $runtimeDirectory ('microsoft.netcore.app.runtime.win-x64.' + $RuntimeVersion + '.nupkg')
    $runtime = [ordered]@{ id = 'Microsoft.NETCore.App.Runtime.win-x64'; version = $RuntimeVersion; license = $runtimeLicense; evidence = $runtimeEvidence; nupkgSha256 = if (Test-Path $runtimeArchive) { Get-LicenseHash $runtimeArchive } else { '' } }
    if (-not $runtime.nupkgSha256) { $blockers.Add(".NET runtime ${RuntimeVersion}: original runtime nupkg is missing") }
    else {
        $runtimeMetadataPath = Join-Path $runtimeDirectory '.nupkg.metadata'
        if (-not (Test-Path $runtimeMetadataPath)) { $blockers.Add(".NET runtime ${RuntimeVersion}: canonical package content hash metadata missing") }
        else {
            $runtimeMetadata = Get-Content $runtimeMetadataPath -Raw | ConvertFrom-Json -AsHashtable
            $runtimeReader = [NuGet.Packaging.PackageArchiveReader]::new($runtimeArchive)
            try { $runtimeContentHash = $runtimeReader.GetContentHash([Threading.CancellationToken]::None, $null) } finally { $runtimeReader.Dispose() }
            $runtime.nugetContentHashSha512 = $runtimeMetadata.contentHash
            $runtime.nugetContentHashVerified = $runtimeContentHash -ceq $runtimeMetadata.contentHash
            if (-not $runtime.nugetContentHashVerified) { $blockers.Add(".NET runtime ${RuntimeVersion}: canonical content hash mismatch") }
        }
        $runtimeZip = [IO.Compression.ZipFile]::OpenRead($runtimeArchive)
        try {
            foreach ($item in $runtimeEvidence) {
                $entryName = [IO.Path]::GetFileName($item.path)
                $entry = @($runtimeZip.Entries | Where-Object { $_.FullName -ieq $entryName }) | Select-Object -First 1
                if (-not $entry) { $blockers.Add(".NET runtime ${RuntimeVersion}: notice absent in original nupkg: $entryName"); continue }
                $entryStream = $entry.Open()
                try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant() } finally { $entryStream.Dispose() }
                if ($entryHash -cne $item.sha256) { $blockers.Add(".NET runtime ${RuntimeVersion}: extracted notice differs from original nupkg: $entryName") }
            }
        } finally { $runtimeZip.Dispose() }
    }
}
$report = [ordered]@{
    schemaVersion = 1; scope = 'Resolved desktop NuGet graph, conservatively including build-only and non-Windows packages; self-contained runtime evidence is separate.'
    verification = 'nuspec metadata, original nupkg canonical NuGet SHA512 content hash against assets, archive SHA256 and byte-verified full license/NOTICE material; not an independent legal opinion or a native-source provenance audit.'
    assetsSha256 = Get-LicenseHash $AssetsPath; targetFrameworks = @($assets.targets.Keys | Sort-Object)
    packageCount = $inventory.Count; packages = @($inventory); bundledRuntime = $runtime
    unresolved = @($blockers); readyForCandidate = $blockers.Count -eq 0
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Write-LicenseJson (Join-Path $OutputDirectory 'inventory.json') $report
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Resolved desktop dependency license inventory')
$lines.Add('')
$lines.Add('Generated from installed NuGet metadata and exact-commit evidence. The JSON inventory is authoritative for hashes and evidence paths. Package graph presence does not mean a non-Windows library is shipped in win-x64.')
$lines.Add('')
$lines.Add('| Package | Version | nuspec license | Preserved evidence files |')
$lines.Add('|---|---|---|---:|')
foreach ($package in $inventory) { $lines.Add('| ' + $package.id + ' | ' + $package.version + ' | ' + $package.licenseType + ': ' + $package.license + ' | ' + $package.evidence.Count + ' |') }
$lines.Add(''); $lines.Add('## Unresolved evidence')
if ($blockers.Count) { foreach ($blocker in $blockers) { $lines.Add('- ' + $blocker) } } else { $lines.Add('No unresolved license-text or package-checksum evidence in this resolved graph. Candidate delivery additionally requires runtime-pack notices and validation of the actual published files.') }
$lines | Set-Content (Join-Path $OutputDirectory 'inventory.md') -Encoding utf8NoBOM
if ($blockers.Count) { throw ('License inventory blocked: ' + ($blockers -join '; ')) }
[pscustomobject]@{ packageCount = $inventory.Count; readyForCandidate = $true; runtimeVersion = $RuntimeVersion; inventory = 'inventory.json' }
