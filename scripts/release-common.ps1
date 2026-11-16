# Shared release-only helpers. Dot-source from release scripts; no application code or source locks are modified.
Set-StrictMode -Version Latest
function Get-ReleaseHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-ReleaseJson([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
function Get-ReleaseRelativePath([string]$Root, [string]$Path) { [IO.Path]::GetRelativePath($Root, $Path).Replace('\','/') }
function Get-ReleaseTestSummary([Collections.Generic.List[object]]$Records) {
    if ($Records.Count -eq 0) { throw 'No test records to summarize.' }
    $passed = 0
    foreach ($record in $Records) { $passed += [int]$record['passed'] }
    return [ordered]@{ tests=$Records.ToArray(); testsPassed=$passed }
}
function Get-ReleaseFiles([string]$Directory) {
    $paths = [string[]]@(Get-ChildItem -LiteralPath $Directory -File -Recurse | ForEach-Object { Get-ReleaseRelativePath $Directory $_.FullName })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    return @($paths | ForEach-Object { Join-Path $Directory $_ })
}
function Copy-ReleaseSnapshot([string]$Root, [string]$Destination) {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($name in @('global.json','Directory.Build.props','Directory.Packages.props','NuGet.Config','LocalDocSearch.slnx','.editorconfig')) {
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Snapshot input missing: $name" }
        $files.Add($path)
    }
    foreach ($folder in @('src','tests','tools')) {
        foreach ($path in (Get-ReleaseFiles (Join-Path $Root $folder))) {
            $relative = Get-ReleaseRelativePath $Root $path
            if ($relative -match '(^|/)(bin|obj|\.tools|\.git|\.vs|artifacts)(/|$)') { continue }
            $leaf = [IO.Path]::GetFileName($path)
            $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
            $isSource = $extension -in @('.cs','.csproj','.axaml','.resx','.ico','.png','.svg','.manifest','.props','.targets')
            $isLock = $leaf -eq 'packages.lock.json'
            $isFixture = $relative -match '^tests/fixtures/(mixed|long-documents|encoding|text)/[^/]+$' -and $extension -in @('.txt','.md','.log','.csv','.docx','.xlsx','.pptx','.pdf','.tmp')
            if ($isSource -or $isLock -or $isFixture) { $files.Add($path) }
        }
    }
    $records = [Collections.Generic.List[object]]::new()
    foreach ($file in ($files | Sort-Object)) {
        $relative = Get-ReleaseRelativePath $Root $file
        $hash = Get-ReleaseHash $file
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file -Destination $target
        if ((Get-ReleaseHash $target) -ne $hash -or (Get-ReleaseHash $file) -ne $hash) { throw "Source changed while snapshotting: $relative" }
        $records.Add([ordered]@{path=$relative; sha256=$hash})
    }
    return @($records)
}
function New-ReleaseZip([string]$Directory, [string]$ZipPath, [string]$TopLevelName) {
    if (Test-Path -LiteralPath $ZipPath) { throw 'ZIP already exists; choose a new candidate version/output root.' }
    $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in (Get-ReleaseFiles $Directory)) {
            $name = $TopLevelName + '/' + (Get-ReleaseRelativePath $Directory $file)
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
            $inputStream = [IO.File]::OpenRead($file); $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
}
function Assert-ReleasePackage([string]$Directory) {
    foreach ($required in @('DocSearch.exe','DocSearch.dll','DocSearch.deps.json','DocSearch.runtimeconfig.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','clrjit.dll','System.Private.CoreLib.dll','av_libglesv2.dll','libSkiaSharp.dll','libHarfBuzzSharp.dll','LICENSE','THIRD-PARTY-NOTICES.md','README.md','skills/docsearch/SKILL.md','docs/dependency-licenses/inventory.json','build-record.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) { throw "Published package missing: $required" }
    }
    $inventory = Get-Content (Join-Path $Directory 'docs/dependency-licenses/inventory.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $inventory.readyForCandidate -or $inventory.unresolved.Count -ne 0 -or -not $inventory.bundledRuntime) { throw 'Package license evidence is incomplete.' }
    if (@($inventory.bundledRuntime.evidence | Where-Object path -Match '(?i)notice').Count -eq 0 -or -not $inventory.bundledRuntime.nugetContentHashVerified) { throw 'Bundled runtime notice/hash evidence missing.' }
    $build = Get-Content (Join-Path $Directory 'build-record.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($build.status -ne 'local-release-candidate' -or $build.rid -ne 'win-x64' -or -not $build.selfContained -or $build.trimmed -or $build.nativeAot -or $build.singleFile -or $build.upx -or $build.testsPassed -lt 1) { throw 'Build record is not a tested normal self-contained local candidate.' }
    if ($build.runtime -ne $inventory.bundledRuntime.version -or $build.dependencyInventorySha256 -ne (Get-ReleaseHash (Join-Path $Directory $build.dependencyInventory))) { throw 'Build record and dependency inventory disagree.' }
    foreach ($test in $build.tests) { if ($test.failed -ne 0 -or $test.skipped -ne 0 -or $test.passed -lt 1 -or $test.total -ne $test.passed) { throw 'Build record contains incomplete/failing test evidence.' } }
    foreach ($item in @($inventory.packages)) {
        if (-not $item.nugetContentHashVerified) { throw "NuGet checksum evidence incomplete: $($item.id)" }
        foreach ($evidence in $item.evidence) {
            $path = Join-Path $Directory ('docs/dependency-licenses/' + $evidence.path)
            if (-not (Test-Path $path) -or (Get-ReleaseHash $path) -ne $evidence.sha256) { throw "License evidence mismatch: $($evidence.path)" }
        }
    }
    foreach ($evidence in $inventory.bundledRuntime.evidence) {
        $path = Join-Path $Directory ('docs/dependency-licenses/' + $evidence.path)
        if (-not (Test-Path $path) -or (Get-ReleaseHash $path) -ne $evidence.sha256) { throw "Runtime notice mismatch: $($evidence.path)" }
    }
    $configuration = Get-Content (Join-Path $Directory 'DocSearch.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $configuration.runtimeOptions.Contains('includedFrameworks') -or $configuration.runtimeOptions.Contains('framework')) { throw 'Runtime configuration is not self-contained.' }
    $framework = @($configuration.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App')
    if ($framework.Count -ne 1 -or $framework[0].version -ne $inventory.bundledRuntime.version) { throw 'Published runtime version and notice inventory disagree.' }
    $deps = Get-Content (Join-Path $Directory 'DocSearch.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $deps.runtimeTarget.name.EndsWith('/win-x64', [StringComparison]::Ordinal)) { throw 'Published RID is not win-x64.' }
    $exe = [IO.File]::ReadAllBytes((Join-Path $Directory 'DocSearch.exe'))
    $peOffset = [BitConverter]::ToInt32($exe, 0x3c)
    if ($exe[0] -ne 0x4d -or $exe[1] -ne 0x5a -or [BitConverter]::ToUInt16($exe,$peOffset+4) -ne 0x8664) { throw 'Apphost is not a Windows x64 PE executable.' }
    $forbidden = @(Get-ReleaseFiles $Directory | Where-Object { (Get-ReleaseRelativePath $Directory $_) -match '(^|/)(\.tools|obj|bin|test-results|build-input|\.git)(/|$)|(^|/)settings\.json$|Tests\.(dll|exe)$|(^|/)runtimes/(linux|osx|browser|win-arm64|win-x86)' })
    if ($forbidden.Count) { throw ('Unexpected development/user/foreign-platform files in package: ' + ($forbidden -join ', ')) }
    return [ordered]@{ selfContained=$true; rid='win-x64'; runtimeVersion=$framework[0].version; packageCount=$inventory.packageCount; fileCount=@(Get-ReleaseFiles $Directory).Count }
}
