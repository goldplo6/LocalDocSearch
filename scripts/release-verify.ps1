#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$CandidateDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$CandidateDirectory = [IO.Path]::GetFullPath($CandidateDirectory)
$zipFiles = @(Get-ChildItem -LiteralPath $CandidateDirectory -Filter '*.zip' -File)
if ($zipFiles.Count -ne 1) { throw 'Expected exactly one candidate ZIP.' }
$zip = $zipFiles[0]
$name = [IO.Path]::GetFileNameWithoutExtension($zip.Name)
$package = Join-Path $CandidateDirectory $name
$validation = Assert-ReleasePackage $package
$checksum = (Get-Content (Join-Path $CandidateDirectory 'SHA256SUMS.txt') -Raw).Trim()
if ($checksum -cne ((Get-ReleaseHash $zip.FullName) + '  ' + $zip.Name)) { throw 'ZIP SHA256 does not match SHA256SUMS.txt.' }
$manifestPath = Join-Path $package 'file-manifest.json'
$manifest = @(Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $manifest) {
    if ($file.path -match '(^|/)\.\.(/|$)|^[\\/]|^[A-Za-z]:' -or -not $seen.Add($file.path)) { throw 'Unsafe or duplicate manifest path.' }
    $path = Join-Path $package $file.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-ReleaseHash $path) -ne $file.sha256) { throw "Published file checksum mismatch: $($file.path)" }
}
$actualFiles = @(Get-ReleaseFiles $package | ForEach-Object { Get-ReleaseRelativePath $package $_ } | Where-Object { $_ -ne 'file-manifest.json' })
if (@(Compare-Object $actualFiles @($manifest | ForEach-Object path)).Count) { throw 'Package file set differs from the manifest.' }
$archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    $expected = @{}
    foreach ($file in $manifest) { $expected[$name + '/' + $file.path] = $file.sha256 }
    $expected[$name + '/file-manifest.json'] = Get-ReleaseHash $manifestPath
    $previous = ''; $zipSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $archive.Entries) {
        if (-not $expected.ContainsKey($entry.FullName) -or -not $zipSeen.Add($entry.FullName)) { throw "Unexpected/duplicate ZIP entry: $($entry.FullName)" }
        if ($previous -and [StringComparer]::Ordinal.Compare($previous,$entry.FullName) -gt 0) { throw 'ZIP entries are not in stable ordinal order.' }
        if ($entry.LastWriteTime.Year -ne 2026 -or $entry.LastWriteTime.Month -ne 1 -or $entry.LastWriteTime.Day -ne 1 -or $entry.LastWriteTime.Hour -ne 0 -or $entry.LastWriteTime.Minute -ne 0 -or $entry.LastWriteTime.Second -ne 0) { throw 'ZIP entry timestamp differs from the documented fixed value.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($hash -cne $expected[$entry.FullName]) { throw "ZIP content checksum mismatch: $($entry.FullName)" }
        $previous = $entry.FullName
    }
    if ($zipSeen.Count -ne $expected.Count) { throw 'ZIP omits manifest files.' }
} finally { $archive.Dispose() }
[pscustomobject]@{ valid=$true; zip=$zip.Name; sha256=Get-ReleaseHash $zip.FullName; files=$actualFiles.Count+1; rid=$validation.rid; runtime=$validation.runtimeVersion }
