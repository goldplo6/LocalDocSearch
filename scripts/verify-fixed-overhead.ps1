#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$MeasurementPath,
    [Parameter(Mandatory)][string]$NativeReportPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'release-common.ps1')
function Read-AuditJson([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Assert-AuditTrue($Value, [string]$Label) {
    if ($Value -isnot [bool] -or -not $Value) { throw $Label }
}
function Assert-AuditHash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-ReleaseHash $Path) -ine $Expected) { throw "Hash mismatch: $Path" }
}
function Resolve-AuditChild([string]$Parent, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) { throw 'Expected relative audit path.' }
    $full = [IO.Path]::GetFullPath((Join-Path $Parent $Relative))
    if (-not $full.StartsWith($Parent.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Path leaves audit root: $Relative" }
    return $full
}
function Get-AuditProperty($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
if (-not $candidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Expected workspace candidate.' }
$out = Join-Path $candidate 'final-audit.json'
if (Test-Path -LiteralPath $out) { throw 'Audit already exists; preserve the existing audit.' }
$measurementFile = (Resolve-Path -LiteralPath $MeasurementPath).Path
$nativeFile = (Resolve-Path -LiteralPath $NativeReportPath).Path
# This verifier reads existing evidence. It does not build, restore, test, measure or launch the GUI.
& (Join-Path $PSScriptRoot 'release-verify.ps1') -CandidateDirectory $candidate | Out-Host
$zips = @(Get-ChildItem -LiteralPath $candidate -Filter '*.zip' -File)
if ($zips.Count -ne 1) { throw 'Expected exactly one candidate ZIP.' }
$name = $zips[0].BaseName
$package = Join-Path $candidate $name
$snapshot = Join-Path $candidate 'build-input'
$record = Read-AuditJson (Join-Path $package 'build-record.json')
if ($record.version -ne '1.0.0-rc.10' -or $record.tests.Count -ne 6) { throw 'Release record mismatch.' }
Assert-AuditTrue $record.selfContained 'Expected a self-contained candidate.'
$trxFiles = @(Get-ChildItem -LiteralPath (Join-Path $candidate 'test-results') -Filter '*.trx' -File)
if ($trxFiles.Count -ne 6) { throw 'Expected six actual TRX reports.' }
$testNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$tests = @(); $total = 0
foreach ($test in $record.tests) {
    $testName = [IO.Path]::GetFileNameWithoutExtension($test.project)
    if (-not $testNames.Add($testName)) { throw 'Duplicate test project.' }
    $trx = Join-Path $candidate ('test-results/' + $testName + '.trx')
    [xml]$xml = Get-Content -LiteralPath $trx -Raw
    $c = $xml.SelectSingleNode('//*[local-name()="Counters"]')
    $results = @($xml.SelectNodes('//*[local-name()="UnitTestResult"]'))
    if ($null -eq $c -or [int]$c.total -le 0 -or [int]$c.executed -ne [int]$c.total -or [int]$c.passed -ne [int]$c.total -or [int]$c.failed -ne 0 -or [int]$c.notExecuted -ne 0 -or $results.Count -ne [int]$c.total) { throw "Incomplete TRX: $testName" }
    if (@($results | Where-Object { $_.outcome -cne 'Passed' }).Count -ne 0) { throw "Non-passing TRX outcome: $testName" }
    if ([int]$test.total -ne [int]$c.total -or [int]$test.passed -ne [int]$c.passed -or [int]$test.failed -ne 0 -or [int]$test.skipped -ne 0) { throw "Build-record/TRX mismatch: $testName" }
    $total += [int]$c.passed
    $tests += [ordered]@{ project=$test.project; total=[int]$c.total; passed=[int]$c.passed; failed=0; skipped=0; trx=$trx; sha256=Get-ReleaseHash $trx }
}
if ($total -ne [int]$record.testsPassed -or $total -ne 1561) { throw 'Unexpected dynamic TRX total for rc10.' }
$sourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $record.sourceInputs) {
    if (-not $sourcePaths.Add($file.path)) { throw 'Duplicate source input.' }
    Assert-AuditHash (Resolve-AuditChild $root $file.path) $file.sha256
    $rid = @($record.candidateRidLocks | Where-Object { $_.path -eq $file.path })
    if ($rid.Count -gt 1) { throw 'Duplicate candidate RID lock.' }
    $snapshotHash = if ($rid.Count -eq 1) { $rid[0].sha256 } else { $file.sha256 }
    Assert-AuditHash (Resolve-AuditChild $snapshot $file.path) $snapshotHash
}
$locks = @($record.sourceInputs | Where-Object { $_.path -match '(^|/)packages\.lock\.json$' })
if ($locks.Count -ne 13 -or @($record.sourceLocks).Count -ne 13) { throw 'Expected thirteen source locks.' }
foreach ($lock in $record.sourceLocks) {
    $inputLock = @($locks | Where-Object { $_.path -eq $lock.path -and $_.sha256 -ieq $lock.sha256 })
    if ($inputLock.Count -ne 1) { throw 'Source lock inventory mismatch.' }
    Assert-AuditHash (Resolve-AuditChild $root $lock.path) $lock.sha256
}
$m = Read-AuditJson $measurementFile
Assert-AuditTrue $m.passed 'Measurement is incomplete or failed.'
if (@($m.groups).Count -ne 44 -or @($m.rows).Count -ne 22 -or @($m.comparisons).Count -ne 11) { throw 'Expected the fixed 44-group measurement matrix.' }
$measurementRoot = (Resolve-Path -LiteralPath $m.output).Path
$binaryIndex = @{}
foreach ($file in $m.binaryFiles) {
    if ($file.version -notin @('old','new')) { throw 'Unexpected binary version.' }
    $key = $file.version + '|' + $file.path.Replace('\','/')
    if ($binaryIndex.ContainsKey($key)) { throw 'Duplicate measured binary.' }
    Assert-AuditHash (Resolve-AuditChild (Join-Path $measurementRoot $file.version) $file.path) $file.sha256
    $binaryIndex[$key] = $file.sha256
}
foreach ($version in @('old','new')) {
    $actual = @(Get-ChildItem -LiteralPath (Join-Path $measurementRoot $version) -File -Recurse)
    $listed = @($m.binaryFiles | Where-Object { $_.version -eq $version })
    if ($actual.Count -ne $listed.Count) { throw 'Measured binary inventory changed.' }
    foreach ($library in @('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll','LocalDocSearch.Benchmarks.dll')) {
        if (-not $binaryIndex.ContainsKey($version + '|' + $library)) { throw 'Required measured assembly missing.' }
    }
}
foreach ($file in $m.binaryFiles | Where-Object { $_.version -eq 'old' -and $_.path -notin @('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll') }) {
    if ($binaryIndex['new|' + $file.path.Replace('\','/')] -ine $file.sha256) { throw 'Unexpected non-library driver difference.' }
}
$baselineRecordPath = (Resolve-Path -LiteralPath $m.baselineBuildRecord).Path
$baselinePackage = Split-Path $baselineRecordPath
$baselineRecord = Read-AuditJson $baselineRecordPath
if ($m.sourceCommit -cne '79f9f314f0bfe658842b9bc6ea6e60066f737d5e' -or $baselineRecord.version -ne '1.0.0-rc.9') { throw 'Baseline source commit/record mismatch.' }
if ((Resolve-Path -LiteralPath $m.source.oldBinaryDirectory).Path -ine $baselinePackage -or (Resolve-Path -LiteralPath $m.source.candidatePackageDirectory).Path -ine $package) { throw 'Measured old/new package provenance mismatch.' }
$baselineSnapshot = (Resolve-Path -LiteralPath (Join-Path (Split-Path $baselinePackage) 'build-input')).Path
if ((Resolve-Path -LiteralPath $m.source.snapshot).Path -ine $baselineSnapshot) { throw 'Baseline source snapshot mismatch.' }
if (@($m.source.verifiedInputs).Count -eq 0) { throw 'Missing baseline source verification.' }
foreach ($inputFile in $m.source.verifiedInputs) {
    $baselineInput = @($baselineRecord.sourceInputs | Where-Object { $_.path -ceq $inputFile.path })
    if ($baselineInput.Count -ne 1 -or $inputFile.sourceSha256 -ine $baselineInput[0].sha256 -or $inputFile.pinnedSha256 -ine $baselineInput[0].sha256) { throw 'Pinned baseline source input mismatch.' }
    $rid = @($baselineRecord.candidateRidLocks | Where-Object { $_.path -ceq $inputFile.path })
    if ($rid.Count -gt 1) { throw 'Duplicate baseline RID lock.' }
    $expectedHash = if ($rid.Count -eq 1) { $rid[0].sha256 } else { $baselineInput[0].sha256 }
    if ($inputFile.snapshotSha256 -ine $expectedHash) { throw 'Recorded baseline snapshot hash mismatch.' }
    Assert-AuditHash (Resolve-AuditChild $baselineSnapshot $inputFile.path) $expectedHash
}
Assert-AuditHash (Join-Path $snapshot 'tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/LocalDocSearch.Benchmarks.dll') $binaryIndex['new|LocalDocSearch.Benchmarks.dll']
$libraryEvidence = @()
foreach ($library in @('LocalDocSearch.Core.dll','LocalDocSearch.Extractors.dll')) {
    $sha = $binaryIndex['new|' + $library]
    Assert-AuditHash (Join-Path $package $library) $sha
    Assert-AuditHash (Join-Path $baselinePackage $library) $binaryIndex['old|' + $library]
    $snapshotLibrary = Join-Path $snapshot ('tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/' + $library)
    $libraryEvidence += [ordered]@{path=$library; measuredNewSha256=$sha; finalPackageSha256=Get-ReleaseHash (Join-Path $package $library); prePublishSnapshotSha256=Get-ReleaseHash $snapshotLibrary; measuredOldSha256=$binaryIndex['old|' + $library]; baselinePackageSha256=Get-ReleaseHash (Join-Path $baselinePackage $library)}
}
$expected = @('coordinates|coordinates','run|many-small','run|large-log','run|mixed','run|dense-file','run|global-budget','literal|short-16','literal|short-32','literal|short-64','literal|none-32','literal|none-64')
$semantics = @('status','scannedFiles','matchedFiles','occurrences','publishedFiles','publishedOccurrences','errorCount','warningCount','suppressedIssueCount','issueDetailsObserved','limitReasonCode','resultOrderSha256','resultContentSha256')
$countFields = @('status','scannedFiles','matchedFiles','occurrences','publishedFiles','publishedOccurrences','errorCount','warningCount','suppressedIssueCount','issueDetailsObserved')
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$references = @{}; $rawEvidence = @(); $performance = 0; $searchChecks = 0; $coordinateChecks = 0
foreach ($group in $m.groups) {
    $key = $group.kind + '|' + $group.scenario
    if ($key -cnotin $expected -or [int]$group.group -notin @(0,1,2,3) -or $group.version -cne @('old','new','new','old')[[int]$group.group] -or -not $seen.Add(([string]$group.group + '|' + $key))) { throw 'Unexpected or duplicate ABBA group.' }
    $reportPath = (Resolve-Path -LiteralPath $group.report).Path
    if (-not $reportPath.StartsWith($measurementRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Raw report is outside measurement directory.' }
    $r = Read-AuditJson $reportPath
    Assert-AuditTrue $r.passed 'Raw measurement failed.'
    if (@($r.rounds).Count -ne 3) { throw 'Expected three performance rounds per raw report.' }
    $roundIds = @($r.rounds | ForEach-Object { [int]$_.round } | Sort-Object -Unique)
    if (($roundIds -join ',') -ne '1,2,3') { throw 'Missing or duplicate performance round.' }
    $driverSha = $binaryIndex[$group.version + '|LocalDocSearch.Benchmarks.dll']
    if ($group.kind -eq 'coordinates') {
        if ($r.command -ne 'overhead-coordinates' -or $r.iterations -ne 500000 -or $r.repeat -ne 3 -or $r.inputCount -ne 42 -or @($r.resultVerification).Count -ne 42 -or $r.inputsSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Coordinate workload mismatch.' }
        foreach ($item in @(@('driver','LocalDocSearch.Benchmarks.dll'),@('core','LocalDocSearch.Core.dll'),@('extractors','LocalDocSearch.Extractors.dll'))) {
            if ($r.($item[0] + 'Sha256') -ine $binaryIndex[$group.version + '|' + $item[1]]) { throw 'Loaded coordinate binary mismatch.' }
        }
        $totals = $r.rounds[0] | Select-Object calls,successCount,sum,checksum
        $signature = $r.inputsSha256 + '|' + (ConvertTo-Json -InputObject @($r.resultVerification) -Depth 8 -Compress) + '|' + ($totals | ConvertTo-Json -Compress)
        foreach ($round in $r.rounds) {
            if ($round.calls -ne 500000 -or ($round | Select-Object calls,successCount,sum,checksum | ConvertTo-Json -Compress) -cne ($totals | ConvertTo-Json -Compress)) { throw 'Coordinate loop totals differ.' }
        }
        $coordinateChecks++
    } else {
        if ($r.scenario -cne $group.scenario) { throw 'Raw search scenario mismatch.' }
        $v = $r.resultVerification
        Assert-AuditTrue $v.passed 'Independent search verification failed.'
        if ($v.phase -cne 'verification-excluded' -or $v.resultContentSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $v.resultOrderSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Missing excluded search digests.' }
        if ($group.kind -eq 'run') {
            Assert-AuditTrue $r.integrity.verifiedBeforeAndAfter 'Search integrity not verified.'
            Assert-AuditTrue $r.integrity.excludedFromSearchTiming 'Integrity work contaminated timing.'
            if ($r.binarySha256 -ine $driverSha -or $r.workers -ne 1) { throw 'Search driver/workers mismatch.' }
            $before = $r.integrity.sourceSha256Before; $after = $r.integrity.sourceSha256After
        } else {
            if ($r.driverSha256 -ine $driverSha -or $r.coreSha256 -ine $binaryIndex[$group.version + '|LocalDocSearch.Core.dll']) { throw 'Loaded literal binary mismatch.' }
            $before = $r.sourceSha256Before; $after = $r.sourceSha256After
        }
        if ($before -notmatch '^[a-fA-F0-9]{64}$' -or $before -cne $after) { throw 'Search source integrity changed.' }
        $signature = ($v | Select-Object -Property $semantics | ConvertTo-Json -Compress) + '|' + $before
        foreach ($round in $r.rounds) {
            foreach ($field in $countFields) { if ($round.$field -cne $v.$field) { throw "Performance/verification counts differ: $field" } }
            if ((Get-AuditProperty $round 'limitReasonCode') -cne (Get-AuditProperty $v 'limitReasonCode')) { throw 'Performance/verification limit reason differs.' }
        }
        $searchChecks++
    }
    foreach ($round in $r.rounds) {
        Assert-AuditTrue $round.passed 'Performance sample failed.'
        if ('resultContentSha256' -in $round.PSObject.Properties.Name -or 'resultOrderSha256' -in $round.PSObject.Properties.Name) { throw 'Performance sample includes independent result hashing.' }
        $performance++
    }
    if ($references.ContainsKey($key)) { if ($references[$key] -cne $signature) { throw "Old/new semantics differ: $key" } } else { $references[$key] = $signature }
    $rawEvidence += [ordered]@{ group=$group.group; version=$group.version; kind=$group.kind; scenario=$group.scenario; path=$reportPath; sha256=Get-ReleaseHash $reportPath }
}
if ($performance -ne 132 -or $searchChecks -ne 40 -or $coordinateChecks -ne 4 -or $references.Count -ne 11) { throw 'Incomplete independent matrix audit.' }
$protected = @{}
if (@($m.protectedBefore).Count -ne 3 -or @($m.protectedAfter).Count -ne 3) { throw 'Protected artifact inventory mismatch.' }
foreach ($file in $m.protectedBefore) {
    if ($protected.ContainsKey($file.path)) { throw 'Duplicate protected artifact.' }
    $after = @($m.protectedAfter | Where-Object { $_.path -ceq $file.path })
    if ($after.Count -ne 1 -or $after[0].sha256 -ine $file.sha256) { throw 'Protected artifact changed during measurement.' }
    Assert-AuditHash $file.path $file.sha256
    $protected[$file.path] = $file.sha256
}
$protectedPaths = @('artifacts/root-relocated/LocalDocSearch/DocSearch.exe','artifacts/releases-literal-prefilter/LocalDocSearch-1.0.0-rc.9-win-x64/LocalDocSearch-1.0.0-rc.9-win-x64.zip','artifacts/releases-literal-prefilter/LocalDocSearch-1.0.0-rc.9-win-x64/LocalDocSearch-1.0.0-rc.9-win-x64/DocSearch.exe')
foreach ($relative in $protectedPaths) { if (-not $protected.ContainsKey((Join-Path $root $relative))) { throw 'Required protected artifact missing.' } }
$n = Read-AuditJson $nativeFile
foreach ($field in @('passed','ownedProcessClosed','normalCloseRequested','sourceFilesUnchanged')) { Assert-AuditTrue $n.$field "Native report failed: $field" }
if ($n.clipboardTouched -cne $false -or $n.globalInputUsed -cne $false -or $null -ne $n.runtimeRoot -or @($n.cases).Count -ne 7) { throw 'Native validation conditions differ.' }
if ([IO.Path]::GetFullPath($n.executable) -ine (Join-Path $package 'DocSearch.exe') -or [IO.Path]::GetFullPath($n.docSearchDll) -ine (Join-Path $package 'DocSearch.dll') -or [IO.Path]::GetFullPath($n.coreClrPath) -ine (Join-Path $package 'coreclr.dll')) { throw 'Native package/runtime path mismatch.' }
Assert-AuditHash (Join-Path $package 'DocSearch.exe') $n.exeSha256
Assert-AuditHash (Join-Path $package 'DocSearch.dll') $n.docSearchDllSha256
Assert-AuditTrue $n.preview.passed 'Native preview failed.'
if ($n.preview.expectedSelectedText -cne 'aba' -or $n.preview.documentValue -cne 'aba 西瓜' -or $n.preview.selectionStatus -cne '已选中 3 个字符 · 行 1 · 列 1') { throw 'Native preview read-back mismatch.' }
$caseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($case in $n.cases) {
    Assert-AuditTrue $case.passed 'Native search case failed.'
    if (-not $caseNames.Add($case.name) -or $case.actualFiles -ne $case.expectedFiles -or $case.actualHits -ne $case.expectedHits -or $case.skippedFiles -ne 0) { throw 'Native search evidence mismatch.' }
}
if (@($n.sourceBefore).Count -eq 0 -or (ConvertTo-Json -InputObject @($n.sourceBefore) -Depth 8 -Compress) -cne (ConvertTo-Json -InputObject @($n.sourceAfter) -Depth 8 -Compress)) { throw 'Native source SHA/length/mtime changed.' }
$documents = @('README.md','docs/fixed-overhead-plan.md','docs/fixed-overhead-results.md','docs/search-principles-and-next-steps.md')
$links = 0; $documentEvidence = @()
foreach ($doc in $documents) {
    $path = Join-Path $root $doc
    $text = Get-Content -LiteralPath $path -Raw
    foreach ($match in [regex]::Matches($text, '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value.Trim()
        if ($target -match '^<([^>]+)>') { $target = $Matches[1] } else { $target = ($target -split '\s+["\x27]',2)[0] }
        if ($target -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|//|#)') { continue }
        $target = [Uri]::UnescapeDataString(($target -split '[#?]',2)[0])
        if ($target -and -not (Test-Path -LiteralPath (Join-Path (Split-Path $path) $target))) { throw "Broken relative link: $doc -> $target" }
        $links++
    }
    $documentEvidence += [ordered]@{path=$doc; sha256=Get-ReleaseHash $path}
}
$remaining = @(Get-CimInstance Win32_Process -Filter "Name='DocSearch.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($candidate + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
if ($remaining.Count -ne 0) { throw 'Owned candidate process remains.' }
$audit = [ordered]@{
    schemaVersion=1; passed=$true; checkedAtUtc=[datetime]::UtcNow.ToString('O'); candidate=$candidate
    testsPassed=$total; tests=$tests; sourceFiles=@($record.sourceInputs).Count; sourceLocks=$locks.Count; sourceUnchanged=$true
    measurement=$measurementFile; measurementSha256=Get-ReleaseHash $measurementFile; measurementGroups=44; performanceSamples=$performance
    independentSearchVerifications=$searchChecks; coordinateVerificationGroups=$coordinateChecks; coordinateInputsPerGroup=42; scenarios=$references.Count; rawReports=$rawEvidence
    supplementalMeasurementPolicy='Separate supplemental reports do not alter the original fixed 44-group matrix and are not certified by this audit.'
    libraries=$libraryEvidence; measuredCandidateLibrariesIdentical=$true; baselinePackage=$baselinePackage; candidatePackage=$package; protectedArtifacts=$m.protectedAfter
    prePublishLibraryDifferenceReason='Release publish applies rc10 Version and CI properties and may rebuild libraries; pre-publish benchmark output hashes are recorded separately. Measured new libraries must exactly match final rc10 package bytes, and measured old libraries must exactly match the rc9 package bytes.'
    nativeReport=$nativeFile; nativeReportSha256=Get-ReleaseHash $nativeFile; nativeSearchCases=$caseNames.Count; previewPassed=$true; sourceDocumentsUnchanged=$true; coreClrFromPackage=$true; ownedCandidateProcesses=0
    documents=$documentEvidence; relativeLinks=$links; brokenRelativeLinks=0; productionDirectoryUpdated=$false
    zipSha256=Get-ReleaseHash $zips[0].FullName; executable=Join-Path $package 'DocSearch.exe'; executableSha256=Get-ReleaseHash (Join-Path $package 'DocSearch.exe')
}
# CreateNew closes the existence-check race; neither a prior audit nor a concurrent audit is overwritten.
$json = $audit | ConvertTo-Json -Depth 16
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine)
$stream = [IO.FileStream]::new($out, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
$json
