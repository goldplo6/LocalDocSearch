#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$BaselineStatePath,
    [Parameter(Mandatory)][string]$CsvMeasurementPath,
    [Parameter(Mandatory)][string]$UiMeasurementPath,
    [Parameter(Mandatory)][string]$NativeReportPath,
    [Parameter(Mandatory)][string]$SearchNativeReportPath,
    [Parameter(Mandatory)][string]$ScrollNativeReportPath,
    [Parameter(Mandatory)][string]$ControlNativeReportPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'release-common.ps1')
$evidence = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
function Require($Condition, [string]$Label) { if (-not $Condition) { throw $Label } }
function Property($Object, [string]$Name) {
    Require ($null -ne $Object) "Missing object for $Name"
    $property = $Object.PSObject.Properties[$Name]
    Require ($null -ne $property) "Missing evidence field: $Name"
    return $property.Value
}
function OptionalProperty($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
function True($Value, [string]$Label) { Require ($Value -is [bool] -and $Value) $Label }
function False($Value, [string]$Label) { Require ($Value -is [bool] -and -not $Value) $Label }
function Canonical($Value) { ConvertTo-Json -InputObject $Value -Depth 40 -Compress }
function HashValue($Value) { Require ($Value -is [string] -and $Value -match '^[a-fA-F0-9]{64}$') 'Missing or invalid SHA256.' }
function SameHash($Left, $Right, [string]$Label) { HashValue $Left; HashValue $Right; Require ($Left -ieq $Right) $Label }
function Child([string]$Parent, [string]$Relative) {
    Require (-not [string]::IsNullOrWhiteSpace($Relative) -and -not [IO.Path]::IsPathRooted($Relative)) 'Expected a relative evidence path.'
    $full = [IO.Path]::GetFullPath((Join-Path $Parent $Relative))
    Require ($full.StartsWith($Parent.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) "Evidence path escapes parent: $Relative"
    return $full
}
function FilePath([string]$Path) {
    $full = (Resolve-Path -LiteralPath $Path).Path
    Require (Test-Path -LiteralPath $full -PathType Leaf) "Expected evidence file: $Path"
    return $full
}
function VerifyHash([string]$Path, $Expected) { SameHash (Get-ReleaseHash $Path) $Expected "SHA256 mismatch: $Path" }
function ReadJson([string]$Path) {
    $full = FilePath $Path; $before = Get-ReleaseHash $full
    $value = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
    SameHash (Get-ReleaseHash $full) $before "Evidence changed while reading: $full"
    if ($evidence.ContainsKey($full)) { SameHash $evidence[$full] $before 'Evidence was replaced.' } else { $evidence.Add($full, $before) }
    return $value
}
function Rounds($Items, [string]$Label) {
    Require (@($Items).Count -eq 3) "Expected three rounds: $Label"
    Require ((@($Items | ForEach-Object { Property $_ 'round' } | Sort-Object) -join ',') -ceq '1,2,3') "Invalid round identities: $Label"
}
function Nonnegative($Value, [string]$Label) {
    Require ($null -ne $Value -and $Value -is [ValueType] -and $Value -isnot [bool] -and [double]::IsFinite([double]$Value) -and [double]$Value -ge 0) "Invalid numeric evidence: $Label"
}
function Median($Values) {
    $sorted = @($Values | Sort-Object); Require ($sorted.Count -gt 0) 'Empty metric samples.'
    if ($sorted.Count % 2) { return [double]$sorted[[int]($sorted.Count / 2)] }
    return ([double]$sorted[$sorted.Count / 2 - 1] + [double]$sorted[$sorted.Count / 2]) / 2
}
function Rows($Rows, $Samples, [string[]]$Keys, $Metrics) {
    Require (@($Rows).Count -eq $Samples.Count) 'Summary row inventory mismatch.'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $Rows) {
        $key = ($Keys | ForEach-Object { Property $row $_ }) -join '|'
        Require ($seen.Add($key) -and $Samples.ContainsKey($key)) 'Duplicate or unexpected metric row.'
        $rounds = @($Samples[$key]); Require ($rounds.Count -eq 6 -and $row.samples -eq 6) 'Expected six samples per version/workload.'
        foreach ($metric in $Metrics) {
            $values = @(foreach ($round in $rounds) {
                $value = $round
                foreach ($part in $metric.path.Split('.')) { $value = Property $value $part }
                Nonnegative $value $metric.path; [double]$value
            })
            $raw = @(Property $row $metric.raw)
            Require ($raw.Count -eq $values.Count) 'Metric raw-array length mismatch.'
            for ($i = 0; $i -lt $values.Count; $i++) { Require ([double]$raw[$i] -eq $values[$i]) 'Summary raw sample differs from report.' }
            Require ([double](Property $row $metric.median) -eq (Median $values)) 'Summary median differs from raw reports.'
        }
    }
}
function SourcePair($Report, [string]$Before, [string]$After) {
    $left = Property $Report $Before; $right = Property $Report $After
    Require ($null -ne $left -and @($left).Count -gt 0 -and (Canonical $left) -ceq (Canonical $right)) 'Source before/after evidence differs.'
    if ($left -is [string]) { HashValue $left }
}
function NativeCommon($Report, [string]$ExeField, [string]$HashField, [string]$SourceField) {
    True $Report.passed 'Native report failed.'; True $Report.ownedProcessClosed 'Owned native process remains.'
    True (Property $Report $SourceField) 'Native sources changed.'
    Require ([IO.Path]::GetFullPath((Property $Report $ExeField)) -ieq $candidateExe) 'Native report belongs to another executable.'
    VerifyHash $candidateExe (Property $Report $HashField)
    False $Report.clipboardTouched 'Native report touched clipboard.'
}

# Existing evidence only: no build, restore, test, benchmark, UI launch, or process termination.
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
Require ($candidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'Expected workspace candidate.'
$out = Join-Path $candidate 'final-audit.json'
Require (-not (Test-Path -LiteralPath $out)) 'Audit exists; preserve it.'
$releaseVerification = & (Join-Path $PSScriptRoot 'release-verify.ps1') -CandidateDirectory $candidate
True $releaseVerification.valid 'Candidate release verification failed.'
$zips = @(Get-ChildItem -LiteralPath $candidate -Filter '*.zip' -File)
Require ($zips.Count -eq 1) 'Expected one candidate ZIP.'
$package = Join-Path $candidate $zips[0].BaseName
$snapshot = Join-Path $candidate 'build-input'
$candidateExe = Join-Path $package 'DocSearch.exe'
$record = ReadJson (Join-Path $package 'build-record.json')
True $record.selfContained 'Expected self-contained release.'
Require (@($record.tests).Count -eq 6) 'Expected six release test projects.'
$tests = @(); $total = 0; $testNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$expectedTests = @('LocalDocSearch.Benchmarks.Tests','LocalDocSearch.Core.Tests','LocalDocSearch.Desktop.Tests','LocalDocSearch.Extractors.Tests','LocalDocSearch.Navigation.Tests','LocalDocSearch.Settings.Tests')
Require (@(Get-ChildItem -LiteralPath (Join-Path $candidate 'test-results') -Filter '*.trx' -File).Count -eq 6) 'Expected six actual TRX files.'
foreach ($test in $record.tests) {
    $name = [IO.Path]::GetFileNameWithoutExtension($test.project)
    Require ($name -cin $expectedTests -and $testNames.Add($name)) 'Unexpected or duplicate test project.'
    $path = FilePath (Join-Path $candidate "test-results/$name.trx")
    $hash = Get-ReleaseHash $path; [xml]$xml = Get-Content -LiteralPath $path -Raw
    $c = $xml.SelectSingleNode('//*[local-name()="Counters"]')
    $results = @($xml.SelectNodes('//*[local-name()="UnitTestResult"]'))
    Require ($null -ne $c -and [int]$c.total -gt 0 -and [int]$c.executed -eq [int]$c.total -and [int]$c.passed -eq [int]$c.total -and [int]$c.failed -eq 0 -and [int]$c.notExecuted -eq 0 -and $results.Count -eq [int]$c.total) "Incomplete TRX: $name"
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $results) { Require ($result.outcome -ceq 'Passed' -and $ids.Add([string]$result.executionId)) "Invalid raw test result: $name" }
    Require ($test.total -eq [int]$c.total -and $test.passed -eq [int]$c.passed -and $test.failed -eq 0 -and $test.skipped -eq 0) 'TRX/build-record counts differ.'
    VerifyHash $path $hash; $evidence.Add($path, $hash); $total += [int]$c.passed
    $tests += [ordered]@{project=$test.project;passed=[int]$c.passed;failed=0;skipped=0;trx=$path;sha256=$hash}
}
Require ($total -eq $record.testsPassed) 'Release test aggregate differs from actual results.'
$inputs = @{}; $ridLocks = @{}
foreach ($lock in $record.candidateRidLocks) { Require (-not $ridLocks.ContainsKey($lock.path)) 'Duplicate candidate RID lock.'; $ridLocks[$lock.path] = $lock.sha256 }
foreach ($file in $record.sourceInputs) {
    Require (-not $inputs.ContainsKey($file.path)) 'Duplicate source input.'; $inputs[$file.path] = $file.sha256
    VerifyHash (Child $root $file.path) $file.sha256
    $sha = if ($ridLocks.ContainsKey($file.path)) { $ridLocks[$file.path] } else { $file.sha256 }
    VerifyHash (Child $snapshot $file.path) $sha
}
foreach ($path in $ridLocks.Keys) { Require ($inputs.ContainsKey($path) -and $path -match '(^|/)packages\.lock\.json$') 'Unexpected candidate RID lock.' }
$locks = @($record.sourceInputs | Where-Object path -Match '(^|/)packages\.lock\.json$')
Require ($locks.Count -eq 13 -and @($record.sourceLocks).Count -eq 13) 'Expected thirteen source locks.'
$lockSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($lock in $record.sourceLocks) { Require ($lockSeen.Add($lock.path) -and $inputs.ContainsKey($lock.path)) 'Duplicate or missing source lock.'; SameHash $lock.sha256 $inputs[$lock.path] 'Source lock inventory differs.' }

$baselineFile = FilePath $BaselineStatePath; $baseline = ReadJson $baselineFile
$baselineRoot = Split-Path $baselineFile; $baselineSnapshot = Join-Path $baselineRoot 'baseline'
Require (@($baseline.sources).Count -eq 150) 'Expected original 150-file baseline hash inventory.'
$baselineSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$baselineProduction = 0; $baselineBackups = 0; $baselineUnchanged = 0; $baselineChanges = @()
$expectedBackups = @('src/LocalDocSearch.Core/CsvTextExtractor.cs','src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs','src/LocalDocSearch.Desktop/MainWindow.axaml.cs')
$allowedBaselineChanges = @('src/LocalDocSearch.Core/CsvTextExtractor.cs','src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs','tests/LocalDocSearch.Desktop.Tests/MixedFormatsTests.cs','tests/LocalDocSearch.Desktop.Tests/OriginalFindTests.cs','tests/LocalDocSearch.Desktop.Tests/PerformanceTests.cs','tests/LocalDocSearch.Desktop.Tests/PrototypeTests.cs','tests/LocalDocSearch.Desktop.Tests/ResultScrollingTests.cs','tests/LocalDocSearch.Desktop.Tests/SettingsAndNavigationTests.cs','tools/LocalDocSearch.Benchmarks/Program.cs')
foreach ($file in $baseline.sources) {
    $relative = $file.path.Replace('\','/')
    Require ($baselineSeen.Add($relative)) 'Duplicate baseline source.'
    if ($relative -cin $expectedBackups) { VerifyHash (Child $baselineSnapshot $relative) $file.sha256; $baselineBackups++ }
    $currentHash = Get-ReleaseHash (Child $root $relative)
    if ($currentHash -ieq $file.sha256) { $baselineUnchanged++ } else {
        Require ($relative -cin $allowedBaselineChanges) "Unexpected captured-baseline change: $relative"
        $baselineChanges += [ordered]@{path=$relative;baselineSha256=$file.sha256;candidateSha256=$currentHash}
    }
    if ($relative.StartsWith('src/', [StringComparison]::Ordinal)) { $baselineProduction++; Require ($inputs.ContainsKey($relative)) 'Baseline production source removed from release.' }
}
Require ($baselineBackups -eq 3 -and @(Get-ChildItem -LiteralPath $baselineSnapshot -File -Recurse).Count -eq 3) 'Expected three original source backups.'
Require ($baselineChanges.Count -eq $allowedBaselineChanges.Count) 'Captured-baseline change inventory differs.'
$baselinePackage = Child $root $baseline.baselinePackage
$baselineOuter = Split-Path $baselinePackage
$baselineRecord = ReadJson (Join-Path $baselinePackage 'build-record.json')
Require ($baselineRecord.version -ceq '1.0.0-rc.10') 'Expected actual rc10 baseline.'
$oldAudit = ReadJson (Join-Path $baselineOuter 'final-audit.json')
True $oldAudit.passed 'Recorded rc10 audit failed.'
Require ([IO.Path]::GetFullPath($oldAudit.executable) -ieq (Join-Path $baselinePackage 'DocSearch.exe')) 'Baseline executable provenance differs.'
VerifyHash $oldAudit.executable $oldAudit.executableSha256
$oldZips = @(Get-ChildItem -LiteralPath $baselineOuter -Filter '*.zip' -File)
Require ($oldZips.Count -eq 1) 'Expected one rc10 ZIP.'; VerifyHash $oldZips[0].FullName $oldAudit.zipSha256
$oldReleaseVerification = & (Join-Path $PSScriptRoot 'release-verify.ps1') -CandidateDirectory $baselineOuter
True $oldReleaseVerification.valid 'Baseline package verification failed.'
# Compare only production inputs with rc10; tests/docs/tools may have intentional prior changes.
$oldProduction = @{}; $newProduction = @{}
foreach ($file in $baselineRecord.sourceInputs | Where-Object path -Like 'src/*') { Require (-not $oldProduction.ContainsKey($file.path)) 'Duplicate rc10 production input.'; $oldProduction[$file.path] = $file.sha256 }
foreach ($file in $record.sourceInputs | Where-Object path -Like 'src/*') { $newProduction[$file.path] = $file.sha256 }
Require ($oldProduction.Count -eq $newProduction.Count) 'Production source inventory changed.'
$allowed = @('src/LocalDocSearch.Core/CsvTextExtractor.cs','src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs')
$changes = @()
foreach ($path in $oldProduction.Keys | Sort-Object) {
    Require ($newProduction.ContainsKey($path)) 'Production source input removed.'
    if ($newProduction[$path] -ine $oldProduction[$path]) { Require ($path -cin $allowed) "Unexpected production change: $path"; $changes += [ordered]@{path=$path;baselineSha256=$oldProduction[$path];candidateSha256=$newProduction[$path]} }
}
Require ($changes.Count -eq 2) 'Expected exactly the two approved production changes.'
foreach ($file in $baseline.sources | Where-Object path -Like 'src*') {
    $relative = $file.path.Replace('\','/'); SameHash $oldProduction[$relative] $file.sha256 'Captured baseline and rc10 production source disagree.'
}

$m = ReadJson $CsvMeasurementPath
True $m.passed 'CSV measurement failed.'
Require (@($m.groups).Count -eq 44 -and @($m.rows).Count -eq 22) 'Expected 44 CSV/control groups and 22 metric rows.'
Require ((Resolve-Path -LiteralPath $m.baseline).Path -ieq $baselinePackage -and (Resolve-Path -LiteralPath $m.candidate).Path -ieq $package) 'CSV old/new package provenance differs.'
$measurementRoot = (Resolve-Path -LiteralPath $m.output).Path
$manifest = ReadJson (Join-Path $measurementRoot 'csv-corpus/csv-manifest.json')
False $manifest.quick 'Expected full-size CSV corpus.'
Require ($manifest.generator -ceq 'LocalDocSearch/csv-parsing-v1' -and @($manifest.files).Count -eq 3) 'CSV corpus manifest differs.'
$corpusFiles = @{}
foreach ($file in $manifest.files) {
    Require ($file.kind -cin @('plain','quoted','boundary') -and -not $corpusFiles.ContainsKey($file.kind)) 'Duplicate or unexpected CSV corpus kind.'
    $path = Child (Join-Path $measurementRoot 'csv-corpus') $file.relativePath
    VerifyHash $path $file.sha256; $item = Get-Item -LiteralPath $path
    Require ($item.Length -eq $file.bytes -and $item.LastWriteTimeUtc.Ticks -eq $file.lastWriteTimeUtcTicks -and $file.bytes -gt 7MB) 'CSV corpus length/mtime/full-size mismatch.'
    $corpusFiles[$file.kind] = $file
}
$binaryIndex = @{}
foreach ($file in $m.binaryFiles) {
    Require ($file.version -cin @('old','new')) 'Invalid measured binary version.'
    $relative = $file.path.Replace('\','/'); $key = $file.version + '|' + $relative
    Require (-not $binaryIndex.ContainsKey($key)) 'Duplicate measured binary.'
    VerifyHash (Child (Join-Path $measurementRoot $file.version) $relative) $file.sha256
    $binaryIndex[$key] = $file.sha256
}
foreach ($version in @('old','new')) {
    $actual = @(Get-ChildItem -LiteralPath (Join-Path $measurementRoot $version) -File -Recurse)
    Require ($actual.Count -eq @($m.binaryFiles | Where-Object version -CEQ $version).Count) 'Measured binary inventory drift.'
    foreach ($library in @('LocalDocSearch.Core.dll','LocalDocSearch.Benchmarks.dll')) { Require ($binaryIndex.ContainsKey("$version|$library")) 'Missing measured assembly.' }
    $from = if ($version -ceq 'old') { $baselinePackage } else { $package }
    VerifyHash (Join-Path $from 'LocalDocSearch.Core.dll') $binaryIndex["$version|LocalDocSearch.Core.dll"]
}
foreach ($file in $m.binaryFiles | Where-Object version -CEQ 'old') {
    $relative = $file.path.Replace('\','/'); if ($relative -ceq 'LocalDocSearch.Core.dll') { continue }
    Require ($binaryIndex.ContainsKey('new|' + $relative)) 'New driver file missing.'
    SameHash $file.sha256 $binaryIndex['new|' + $relative] 'Non-Core benchmark dependency differs.'
}
Require (@($m.binaryFiles | Where-Object version -CEQ 'old').Count -eq @($m.binaryFiles | Where-Object version -CEQ 'new').Count) 'Old/new benchmark file sets differ.'
$protected = @{}; Require (@($m.protectedBefore).Count -eq 5 -and @($m.protectedAfter).Count -eq 5) 'Expected five protected artifacts.'
foreach ($file in $m.protectedBefore) {
    Require (-not $protected.ContainsKey($file.path)) 'Duplicate protected artifact.'
    $after = @($m.protectedAfter | Where-Object path -CEQ $file.path)
    Require ($after.Count -eq 1) 'Protected artifact missing after measurement.'
    SameHash $after[0].sha256 $file.sha256 'Protected artifact changed.'; VerifyHash $file.path $file.sha256; $protected[$file.path] = $file.sha256
}
foreach ($path in @((Join-Path $baselinePackage 'LocalDocSearch.Core.dll'),(Join-Path $package 'LocalDocSearch.Core.dll'),(Join-Path $baselinePackage 'DocSearch.exe'),(Join-Path $root 'artifacts/root-relocated/LocalDocSearch/DocSearch.exe'),$oldZips[0].FullName)) { Require ($protected.ContainsKey($path)) 'Protected inventory omitted required artifact.' }
$expectedWorkloads = @('csv|direct-plain','csv|direct-quoted','csv|direct-boundary','csv|engine-sparse-plain','csv|engine-sparse-quoted','csv|engine-sparse-boundary','csv|engine-none-plain','csv|engine-none-quoted','csv|engine-none-boundary','control|many-small','control|mixed')
$semanticFields = @('status','scannedFiles','matchedFiles','occurrences','publishedFiles','publishedOccurrences','errorCount','warningCount','suppressedIssueCount','issueDetailsObserved','limitReasonCode','resultOrderSha256','resultContentSha256')
$countFields = $semanticFields | Where-Object { $_ -notlike '*Sha256' -and $_ -cne 'limitReasonCode' }
$groupSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$rawPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$signatures = @{}; $csvSamples = @{}; $raw = @(); $sampleCount = 0; $verificationCount = 0; $controlCount = 0; $csvGroupCount = 0
foreach ($group in $m.groups) {
    $key = $group.kind + '|' + $group.scenario; $g = Property $group 'group'
    Require ($key -cin $expectedWorkloads -and $g -in @(0,1,2,3) -and $group.version -ceq @('old','new','new','old')[[int]$g] -and $groupSeen.Add("$g|$key")) 'Unexpected or duplicate ABBA workload.'
    $reportPath = FilePath $group.report
    Require ($reportPath.StartsWith($measurementRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and $rawPaths.Add($reportPath)) 'Reused or external raw CSV report.'
    $r = ReadJson $reportPath; True $r.passed 'Raw CSV/control report failed.'; Rounds $r.rounds $key
    Require ($r.scenario -ceq $group.scenario -and $r.workers -eq 1) 'Raw workload/workers mismatch.'
    if ($group.kind -ceq 'csv') {
        $csvGroupCount++
        SameHash $r.coreSha256 $binaryIndex[$group.version + '|LocalDocSearch.Core.dll'] 'Loaded CSV Core differs from package.'
        SameHash $r.driverSha256 $binaryIndex[$group.version + '|LocalDocSearch.Benchmarks.dll'] 'Loaded CSV driver differs.'
        SourcePair $r 'sourceSha256Before' 'sourceSha256After'; Rounds $r.verifications 'CSV excluded verification'
        Require ($r.expected.scenario -ceq $group.scenario -and $corpusFiles.ContainsKey($r.expected.kind)) 'Unexpected CSV standard corpus.'
        $corpusFile = $corpusFiles[$r.expected.kind]
        foreach ($field in @('bytes','recordCount','fieldCount','textCharacters','maxFieldCharacters')) { Require ((Property $r.expected $field) -eq (Property $corpusFile $field)) 'CSV raw expectation differs from actual corpus manifest.' }
        $expectedMode = if ($group.scenario.StartsWith('direct-', [StringComparison]::Ordinal)) { 'direct' } else { 'engine' }
        $expectedOccurrences = if ($group.scenario.StartsWith('engine-sparse-', [StringComparison]::Ordinal)) { 1 } else { 0 }
        Require ($r.expected.mode -ceq $expectedMode -and $r.expected.engineOccurrences -eq $expectedOccurrences) 'CSV raw mode/hit expectation differs.'
        $signature = $null
        foreach ($v in $r.verifications) {
            True $v.passed 'CSV excluded verification failed.'
            Require ($v.phase -ceq 'after-round-excluded' -and @($v.mismatches).Count -eq 0) 'Invalid CSV verification phase/mismatches.'
            SameHash $v.segmentSha256 $v.expectedSegmentSha256 'CSV segment digest differs from independent oracle.'
            SameHash $v.engineResultSha256 $v.expectedEngineResultSha256 'CSV engine digest differs from independent oracle.'
            HashValue $v.extractionWarningsSha256; HashValue $v.engineIssuesSha256
            foreach ($mode in @('direct','engine')) {
                $c = Property $v ($mode + 'Counts'); $direct = $mode -ceq 'direct'
                Require ($c.status -ceq 'Completed' -and $c.scannedFiles -eq 1 -and $c.publishedFiles -eq $(if (-not $direct -and $r.expected.engineOccurrences -eq 1) { 1 } else { 0 }) -and $c.occurrences -eq $(if ($direct) { 0 } else { $r.expected.engineOccurrences }) -and $c.segmentCount -eq $(if ($direct) { $r.expected.fieldCount } else { 0 }) -and $c.recordCount -eq $(if ($direct) { $r.expected.recordCount } else { 0 }) -and $c.textCharacters -eq $(if ($direct) { $r.expected.textCharacters } else { 0 }) -and $c.warnings -eq 0 -and $c.errors -eq 0 -and $c.issueDetails -eq 0) 'CSV independent counts mismatch.'
            }
            $s = Canonical ($v | Select-Object segmentSha256,extractionWarningsSha256,engineResultSha256,engineIssuesSha256,directCounts,engineCounts)
            if ($null -eq $signature) { $signature = $s } else { Require ($signature -ceq $s) 'CSV verification rounds disagree.' }
            $timed = @($r.rounds | Where-Object round -EQ $v.round)
            Require ($timed.Count -eq 1 -and (Canonical $timed[0].counts) -ceq (Canonical (Property $v ($r.expected.mode + 'Counts')))) 'CSV timed/independent counts differ.'
            $verificationCount++
        }
        $signature += '|' + $r.sourceSha256Before + '|' + (Canonical $r.expected)
    } else {
        $v = $r.resultVerification; True $v.passed 'Independent control digest verification failed.'
        Require ($v.phase -ceq 'verification-excluded') 'Control hashing was not excluded.'
        HashValue $v.resultOrderSha256; HashValue $v.resultContentSha256
        True $r.integrity.verifiedBeforeAndAfter 'Control source integrity missing.'; True $r.integrity.excludedFromSearchTiming 'Control integrity included in timer.'
        SourcePair $r.integrity 'sourceSha256Before' 'sourceSha256After'
        SameHash $r.binarySha256 $binaryIndex[$group.version + '|LocalDocSearch.Benchmarks.dll'] 'Loaded control driver differs.'
        foreach ($round in $r.rounds) {
            foreach ($field in $countFields) { Require ((Property $round $field) -ceq (Property $v $field)) "Control timed/independent counts differ: $field" }
            Require ((Canonical (OptionalProperty $round 'limitReasonCode')) -ceq (Canonical (OptionalProperty $v 'limitReasonCode'))) 'Control limit reason differs.'
        }
        $signature = (Canonical ($v | Select-Object -Property $semanticFields)) + '|' + $r.integrity.sourceSha256Before; $controlCount++
    }
    if ($signatures.ContainsKey($key)) { Require ($signatures[$key] -ceq $signature) "Old/new semantics differ: $key" } else { $signatures[$key] = $signature }
    $sampleKey = $key + '|' + $group.version; if (-not $csvSamples.ContainsKey($sampleKey)) { $csvSamples[$sampleKey] = @() }
    foreach ($round in $r.rounds) {
        True $round.passed 'CSV/control timed sample failed.'
        Require (@($round.PSObject.Properties.Name | Where-Object { $_ -match '(?i)sha256|digest|verification' }).Count -eq 0) 'Independent hashing leaked into timed sample.'
        $sampleCount++; $csvSamples[$sampleKey] += $round
    }
    $raw += [ordered]@{group=$g;version=$group.version;kind=$group.kind;scenario=$group.scenario;path=$reportPath;sha256=$evidence[$reportPath]}
}
Require ($csvGroupCount -eq 36 -and $sampleCount -eq 132 -and $verificationCount -eq 108 -and $controlCount -eq 8 -and $signatures.Count -eq 11) 'Incomplete CSV/control matrix.'
Rows $m.rows $csvSamples @('kind','scenario','version') @(
    @{path='elapsedMilliseconds';raw='elapsedRaw';median='elapsedMs'},@{path='cpuMilliseconds';raw='cpuRaw';median='cpuMs'},
    @{path='memory.totalAllocatedBytesDelta';raw='allocatedRaw';median='allocatedBytes'},@{path='memory.processPeakWorkingSetBytesLifetime';raw='peakRaw';median='peakWorkingSetBytesLifetime'})

$u = ReadJson $UiMeasurementPath; True $u.passed 'UI measurement failed.'
Require (@($u.groups).Count -eq 8 -and @($u.rows).Count -eq 4 -and [IO.Path]::GetFullPath($u.oldExe) -ieq (Join-Path $baselinePackage 'DocSearch.exe') -and [IO.Path]::GetFullPath($u.newExe) -ieq $candidateExe) 'UI matrix/provenance mismatch.'
$uiSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$processes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$settingsSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$uiSources = @{}; $uiSamples = @{}; $uiRaw = @(); $uiCount = 0
foreach ($group in $u.groups) {
    $g = Property $group 'group'; $scenario = $group.scenario
    Require ($scenario -cin @('many-small','dense-folded') -and $g -in @(0,1,2,3) -and $group.version -ceq @('old','new','new','old')[[int]$g] -and $uiSeen.Add("$g|$scenario")) 'Unexpected UI ABBA group.'
    $path = FilePath $group.report; Require ($rawPaths.Add($path)) 'Reused UI raw report.'
    $r = ReadJson $path; True $r.passed 'Raw UI report failed.'; True $r.ownedProcessClosed 'UI process remains.'
    False $r.clipboardTouched 'UI measurement touched clipboard.'; False $r.globalInput 'UI measurement used global input.'
    SourcePair $r 'sourceBefore' 'sourceAfter'; Rounds $r.rounds 'UI independent processes'
    $from = if ($group.version -ceq 'old') { $baselinePackage } else { $package }
    Require ([IO.Path]::GetFullPath($r.exe) -ieq (Join-Path $from 'DocSearch.exe')) 'UI raw executable provenance differs.'
    foreach ($item in @(@('exeSha256','DocSearch.exe'),@('desktopSha256','DocSearch.dll'),@('coreSha256','LocalDocSearch.Core.dll'))) {
        VerifyHash (Join-Path $from $item[1]) (Property $r $item[0]); SameHash (Property $r $item[0]) (Property $group $item[0]) 'UI summary/raw binary differs.'
    }
    if ($uiSources.ContainsKey($scenario)) { Require ($uiSources[$scenario] -ceq $r.sourceBefore) 'UI corpus differs across versions.' } else { $uiSources[$scenario] = $r.sourceBefore }
    $files = if ($scenario -ceq 'many-small') { 5000 } else { 2000 }; $hits = if ($scenario -ceq 'many-small') { 10000 } else { 120000 }
    Require ($r.expectedFiles -eq $files -and $r.expectedOccurrences -eq $hits) 'UI expected workload differs.'
    $key = $scenario + '|' + $group.version; if (-not $uiSamples.ContainsKey($key)) { $uiSamples[$key] = @() }
    foreach ($round in $r.rounds) {
        True $round.passed 'UI process sample failed.'; True $round.ownedProcessClosed 'UI sample process remains.'
        Require ($round.files -eq $files -and $round.occurrences -eq $hits -and $round.status -like '已完成*' -and ($round.progress -replace ',','') -like "*已展示文件 $files · 已展示匹配 $hits ·*") 'UI result readback differs.'
        Require ($round.diagnostics -match '本次 ([0-9,]+) ms') 'Missing UI application timer readback.'
        Require ([double]($Matches[1] -replace ',','') -eq [double]$round.endToEndMillisecondsRounded) 'UI application timer differs from diagnostic text.'
        $processId = Property $round 'processId'; $start = Property $round 'processStartTimeUtc'; $settings = Property $round 'settingsPath'
        Require ([long]$processId -gt 0 -and -not [string]::IsNullOrWhiteSpace($start) -and $processes.Add("$processId|$start") -and $settingsSeen.Add([IO.Path]::GetFullPath($settings))) 'UI samples did not use independent process/settings identities.'
        $uiSamples[$key] += $round; $uiCount++
    }
    $uiRaw += [ordered]@{group=$g;version=$group.version;scenario=$scenario;path=$path;sha256=$evidence[$path]}
}
Require ($uiCount -eq 24 -and $processes.Count -eq 24) 'Expected 24 independent UI process samples.'
Rows $u.rows $uiSamples @('scenario','version') @(
    @{path='endToEndMillisecondsRounded';raw='appRaw';median='appElapsedMs'},@{path='externalElapsedMilliseconds';raw='externalRaw';median='externalElapsedMs'},
    @{path='searchCpuMilliseconds';raw='cpuRaw';median='cpuMs'},@{path='peakWorkingSetBytesLifetime';raw='peakRaw';median='peakWorkingSetBytesLifetime'},
    @{path='workingSetBytesAtEnd';raw='workingSetRaw';median='workingSetBytes'},@{path='maximumSampledPrivateBytes';raw='privateRaw';median='sampledPrivateBytes'})

$search = ReadJson $SearchNativeReportPath; NativeCommon $search 'executable' 'exeSha256' 'sourceFilesUnchanged'
True $search.normalCloseRequested 'Native search did not close normally.'; False $search.globalInputUsed 'Native search used global input.'
Require ($null -eq $search.runtimeRoot -and [IO.Path]::GetFullPath($search.coreClrPath) -ieq (Join-Path $package 'coreclr.dll') -and [IO.Path]::GetFullPath($search.docSearchDll) -ieq (Join-Path $package 'DocSearch.dll')) 'Native search runtime/DLL provenance differs.'
VerifyHash $search.docSearchDll $search.docSearchDllSha256; SourcePair $search 'sourceBefore' 'sourceAfter'
Require (@($search.cases).Count -eq 7) 'Expected seven existing native search cases.'
$caseSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$searchCounts = @{
    'prefilter-32-long-no-candidate'=@(0,0); 'prefilter-32-late-sparse-overlap-unicode'=@(5,12)
    'long-no-candidate-four-or'=@(0,0); 'sparse-overlap-unicode-short-four-or'=@(5,12)
    'four-and-qualification'=@(1,4); 'eight-or-supplementary-accented-unicode'=@(6,16); 'two-term-fallback'=@(5,8)
}
foreach ($case in $search.cases) {
    True $case.passed 'Native search case failed.'
    Require ($searchCounts.ContainsKey($case.name) -and $caseSeen.Add($case.name)) 'Unexpected or duplicate native search case.'
    $counts = $searchCounts[$case.name]
    Require ($case.actualFiles -eq $counts[0] -and $case.expectedFiles -eq $counts[0] -and $case.actualHits -eq $counts[1] -and $case.expectedHits -eq $counts[1] -and $case.skippedFiles -eq 0) 'Native search raw case counts differ.'
}
True $search.preview.passed 'Native search preview failed.'
Require ($search.preview.expectedSelectedText -ceq 'aba' -and $search.preview.documentValue -ceq 'aba 西瓜' -and $search.preview.selectionStatus -ceq '已选中 3 个字符 · 行 1 · 列 1') 'Native search preview readback differs.'

$scroll = ReadJson $ScrollNativeReportPath; NativeCommon $scroll 'executable' 'executableSha256' 'sourceUnchanged'
foreach ($field in @('lastItemExpanded','firstBatchLastMatchVisible','allLoadedLastMatchVisible','expandedAfterRecycle','lastMatchReachableAfterRecycle')) { True (Property $scroll $field) "Native wheel evidence failed: $field" }
Require ($scroll.documentCount -eq 300 -and $scroll.wheelMessages -gt 0 -and $scroll.recycleWheelMessages -gt 0 -and $scroll.recycleWheelMessages -le $scroll.recycleWheelBudget -and ($scroll.progress -replace ',','') -like '*已展示文件 300 · 已展示匹配 369*' -and $scroll.lastBeforeExpand.offscreen -ceq $false) 'Native wheel raw workload/recycle evidence differs.'

$control = ReadJson $ControlNativeReportPath; NativeCommon $control 'exe' 'exeSha256' 'sourceFilesUnchanged'
False $control.globalInputUsed 'Native control report used global input.'
Require ($control.workers -eq 1 -and $null -eq $control.runtimeRoot -and @($control.scenarios).Count -eq 4) 'Native single-worker controls inventory differs.'
$controlSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($scenario in $control.scenarios) {
    True $scenario.passed 'Native control scenario failed.'
    Require ($scenario.name -cin @('pause-resume-complete','stop-while-paused','stop-while-running','restart-after-stop') -and $controlSeen.Add($scenario.name)) 'Unexpected/duplicate native control scenario.'
    if ($scenario.name -cin @('pause-resume-complete','stop-while-paused')) {
        $p = $scenario.pause; Require ($p.stableForAtLeastMilliseconds -ge 600 -and @($p.observations).Count -eq 6) 'Missing acknowledged pause observations.'
        foreach ($observation in $p.observations) { Require ($observation -ceq $p.progress) 'Paused native progress changed.' }
    }
    if ($scenario.name -cin @('pause-resume-complete','restart-after-stop')) { Require ($scenario.completion.status -like '已完成*' -and ($scenario.completion.progress -replace ',','') -like '*已展示文件 5000 · 已展示匹配 10000 ·*' -and $scenario.completion.visibleFiles -eq 5000) 'Native control completion readback differs.' }
    if ($scenario.name -cin @('stop-while-paused','stop-while-running')) {
        $stop = $scenario.stop; Require ($stop.status -match '^已(停止|取消)' -and $stop.retainedVisibleFiles -ge $stop.beforeRequestVisibleFiles -and $stop.retainedVisibleFiles -lt 5000) 'Native stop retained-count evidence differs.'
        if ($scenario.name -ceq 'stop-while-paused') { Require ($stop.retainedVisibleFiles -eq $scenario.pause.visibleFiles) 'Paused stop changed visible results.' }
    }
}

# Exact schema emitted by verify-ui-csv-uia.ps1: two cases, two expansions, nine positions plus pop-out.
$native = ReadJson $NativeReportPath
NativeCommon $native 'executable' 'exeSha256' 'sourceFilesUnchanged'
True $native.normalCloseRequested 'CSV native application did not close normally.'
True $native.coreClrFromPackage 'CSV CoreCLR provenance was not verified.'
False $native.globalInputUsed 'CSV native report used global input.'
False $native.originalFileOpened 'CSV native probe opened an external document.'
Require ([IO.Path]::GetFullPath($native.coreClrPath) -ieq (Join-Path $package 'coreclr.dll') -and [IO.Path]::GetFullPath($native.docSearchDll) -ieq (Join-Path $package 'DocSearch.dll')) 'CSV native runtime/DLL did not come from package.'
VerifyHash $native.docSearchDll $native.docSearchDllSha256
SourcePair $native 'sourceBefore' 'sourceAfter'
Require (@($native.cleanupErrors).Count -eq 0 -and @($native.cases).Count -eq 2 -and @($native.expansions).Count -eq 2 -and @($native.previews).Count -eq 10) 'Incomplete native CSV evidence.'
$nativeCorpus = Join-Path (Split-Path (FilePath $NativeReportPath)) 'corpus'
foreach ($file in $native.sourceBefore) {
    $path = Child $nativeCorpus $file.path; VerifyHash $path $file.sha256; $item = Get-Item -LiteralPath $path
    Require ($item.Length -eq $file.length -and $item.LastWriteTimeUtc.Ticks -eq $file.mtimeUtcTicks) 'Native CSV source stamp drift.'
}
$csvCases = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($case in $native.cases) {
    True $case.passed 'CSV native case failed.'
    Require ($case.name -cin @('valid-csv-exact','late-malformed-csv-is-atomic') -and $csvCases.Add($case.name)) 'Unexpected/duplicate CSV native case.'
    $partial = $case.name -ceq 'late-malformed-csv-is-atomic'; $skipped = if ($partial) { 1 } else { 0 }
    Require ($case.expected.scannedFiles -eq 2 + $skipped -and $case.expected.publishedFiles -eq 2 -and $case.expected.hits -eq 9 -and $case.expected.skippedFiles -eq $skipped -and $case.expected.partial -ceq $partial) 'Native CSV expected workload differs.'
    Require ($case.actual.scannedFiles -eq 2 + $skipped -and $case.actual.publishedFiles -eq 2 -and $case.actual.hits -eq 9 -and $case.actual.skippedFiles -eq $skipped -and @($case.actual.filenames).Count -eq 2 -and (@($case.actual.filenames | Sort-Object) -join '|') -ceq 'valid-cr.csv|valid-crlf.csv') 'Native CSV actual publication differs.'
    Require (($case.actual.progress -replace ',','') -ceq "已扫描 $(2 + $skipped) · 已展示文件 2 · 已展示匹配 9 · 跳过/失败 $skipped") 'Native CSV progress readback differs.'
    Require ($case.actual.status -like $(if ($partial) { '部分完成*' } else { '已完成*' })) 'Native CSV terminal state differs.'
    Require (@($case.actual.perFile).Count -eq 2) 'Missing CSV per-file readback.'
    foreach ($pair in @(@('valid-crlf.csv',5),@('valid-cr.csv',4))) {
        $perFile = @($case.actual.perFile | Where-Object file -CEQ $pair[0])
        Require ($perFile.Count -eq 1 -and $perFile[0].hits -eq $pair[1] -and $perFile[0].countLabel -ceq "$($pair[1]) 处匹配") 'CSV per-file match readback differs.'
    }
    if ($partial) {
        True $case.issue.matchedEarlyButFileUnpublished 'Late malformed CSV leaked partial results.'
        Require ($case.issue.code -ceq 'InvalidData' -and $case.issue.message -ceq '读取失败：CSV 格式错误：记录 3、列 2 的引号字段未闭合。' -and [IO.Path]::GetFullPath($case.issue.path) -ieq (Join-Path $nativeCorpus 'broken/broken-late-quote.csv')) 'Native late CSV error code/message/path differs.'
    } else { Require ($null -eq $case.issue) 'Valid native CSV had an issue.' }
}
$expectedPositions = @(
    @('valid-crlf.csv',2,2,2,'西瓜','西瓜,苹果'),@('valid-crlf.csv',2,2,2,'苹果','西瓜,苹果'),
    @('valid-crlf.csv',2,3,2,'西瓜','他说"西瓜"'),@('valid-crlf.csv',3,2,3,'苹果',"上半`n苹果"),@('valid-crlf.csv',3,3,4,'西瓜','尾部西瓜'),
    @('valid-cr.csv',2,2,2,'苹果',"苹果`n西瓜"),@('valid-cr.csv',2,2,2,'西瓜',"苹果`n西瓜"),
    @('valid-cr.csv',3,2,4,'西瓜','逗号,西瓜'),@('valid-cr.csv',3,3,4,'苹果','苹果"派"'))
$previewSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$positionSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($preview in $native.previews) {
    True $preview.passed 'CSV native preview failed.'; True $preview.readOnly 'CSV preview was editable.'
    $expected = @($expectedPositions | Where-Object { $_[0] -ceq $preview.file -and $_[1] -eq $preview.record -and $_[2] -eq $preview.field -and $_[4] -ceq $preview.term })
    Require ($expected.Count -eq 1) 'Unexpected CSV preview position.'
    $p = $expected[0]; $source = "记录 $($p[1]) · 字段列 $($p[2]) · 字段起始行 $($p[3])"
    Require ($preview.fieldStartLine -eq $p[3] -and $preview.expectedDocument -ceq $p[5] -and $preview.documentValue -ceq $p[5] -and $preview.sourceFeedback -ceq $source -and $preview.selectionStatus -ceq "已选中 2 个字符 · $source") 'CSV extracted field/source/selection readback differs.'
    Require ($preview.host -cin @('inline','window')) 'Unexpected CSV preview host.'
    $key = "$($p[0])|$($p[1])|$($p[2])|$($p[4])"
    Require ($previewSeen.Add($key + '|' + $preview.host)) 'Duplicate CSV preview host evidence.'; $null = $positionSeen.Add($key)
    if ($preview.nativeTextPatternSupported -ceq $true) { Require ($preview.independentlySelectedText -ceq $preview.term) 'Independent CSV native selection differs.' }
    else { False $preview.nativeTextPatternSupported 'Missing native selection capability evidence.'; Require ($null -eq $preview.independentlySelectedText) 'Unsupported native selection claimed selected text.' }
}
Require ($positionSeen.Count -eq 9 -and @($native.previews | Where-Object host -CEQ 'inline').Count -gt 0 -and @($native.previews | Where-Object host -CEQ 'window').Count -gt 0) 'Missing exact CSV positions or pop-out preview.'
$expandedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($expansion in $native.expansions) {
    True $expansion.passed 'CSV expansion failed.'; True $expansion.collapsedAfterChecks 'CSV result was not collapsed after checks.'
    True $expansion.finalPageButtonPresent 'CSV final-page placeholder missing.'; False $expansion.finalPageButtonEnabled 'CSV final-page placeholder remained enabled.'
    Require ($expansion.file -cin @('valid-crlf.csv','valid-cr.csv') -and $expandedFiles.Add($expansion.file)) 'Duplicate or unknown CSV expansion.'
    $hits = if ($expansion.file -ceq 'valid-crlf.csv') { 5 } else { 4 }
    Require ($expansion.loadedText -ceq "已展示 $hits / $hits 处" -and $expansion.mechanism -cin @('ExpandCollapsePattern','TogglePattern')) 'CSV expansion/footer evidence differs.'
    $expectedLabels = @($expectedPositions | Where-Object { $_[0] -ceq $expansion.file } | ForEach-Object { "记录 $($_[1]) · 字段列 $($_[2]) · 字段起始行 $($_[3]) · 关键词：$($_[4])" })
    Require ((Canonical @($expansion.positions | Sort-Object)) -ceq (Canonical @($expectedLabels | Sort-Object))) 'CSV expanded position labels differ.'
}

$documents = @('README.md','docs/README.md','docs/ui-csv-optimization-plan.md','docs/ui-csv-optimization-results.md','docs/search-principles-and-next-steps.md')
$docEvidence = @(); $linkCount = 0
foreach ($doc in $documents) {
    $path = FilePath (Child $root $doc); $text = Get-Content -LiteralPath $path -Raw
    foreach ($match in [regex]::Matches($text, '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value.Trim()
        if ($target -match '^<([^>]+)>') { $target = $Matches[1] } else { $target = ($target -split '\s+["\x27]',2)[0] }
        if ($target -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|//|#)') { continue }
        $target = [Uri]::UnescapeDataString(($target -split '[#?]',2)[0])
        if ($target) { Require (Test-Path -LiteralPath (Join-Path (Split-Path $path) $target)) "Broken relative documentation link: $doc -> $target"; $linkCount++ }
    }
    $docEvidence += [ordered]@{path=$doc;sha256=Get-ReleaseHash $path}
}
# Re-read hashes after all checks; the audit certifies a stable evidence set.
foreach ($file in $record.sourceInputs) { VerifyHash (Child $root $file.path) $file.sha256 }
foreach ($path in $evidence.Keys) { VerifyHash $path $evidence[$path] }
VerifyHash $oldZips[0].FullName $oldAudit.zipSha256; VerifyHash $oldAudit.executable $oldAudit.executableSha256
$remaining = @(Get-CimInstance Win32_Process -Filter "Name='DocSearch.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($candidate + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
Require ($remaining.Count -eq 0) 'Owned candidate process remains.'
$audit = [ordered]@{
    schemaVersion=1;passed=$true;checkedAtUtc=[datetime]::UtcNow.ToString('O');candidate=$candidate;candidatePackage=$package
    testsPassed=$total;tests=$tests;sourceInputs=$inputs.Count;sourceLocks=$locks.Count;candidateRidLocks=$ridLocks.Count;baselineSources=@($baseline.sources).Count;baselineProductionSources=$baselineProduction;baselineBackups=$baselineBackups;baselineUnchanged=$baselineUnchanged;baselineChanges=$baselineChanges;releaseProductionInputs=$newProduction.Count;approvedProductionChanges=$changes
    csvGroups=$csvGroupCount;controlGroups=$controlCount;measurementGroups=$groupSeen.Count;performanceSamples=$sampleCount;excludedCsvVerifications=$verificationCount;independentControlDigests=$controlCount;csvScenarios=$signatures.Count;rawCsvReports=$raw
    uiGroups=$uiSeen.Count;uiIndependentProcesses=$processes.Count;uiSamples=$uiCount;rawUiReports=$uiRaw;nativeSearchCases=$caseSeen.Count;nativeControlScenarios=$controlSeen.Count;nativeCsvCases=@($native.cases).Count;nativeCsvPreviews=@($native.previews).Count;nativeWheelDocuments=$scroll.documentCount
    baselinePackage=$baselinePackage;baselineZipSha256=$oldAudit.zipSha256;baselineExecutableSha256=$oldAudit.executableSha256;protectedArtifacts=$m.protectedAfter
    evidence=@(foreach ($path in $evidence.Keys | Sort-Object) { [ordered]@{path=$path;sha256=$evidence[$path]} });documents=$docEvidence;relativeLinks=$linkCount;brokenRelativeLinks=0
    checks=@('release-verify candidate and rc10','six raw TRX: every result Passed, zero skipped','current and snapshot sourceInputs SHA256; thirteen lock files','150 captured baseline hashes, three source backups, 141 unchanged and nine approved changed files; production-only two-file delta','rc10 ZIP/executable preserve recorded hashes','44 ABBA groups; 132 timed samples; 108 excluded CSV verifications; eight control digests','measured Core equals old/new package bytes; shared benchmark driver','summary samples and medians recomputed from raw reports','eight UI groups, 24 independent process/settings identities; EXE/DLL/Core hashes','CSV/search/wheel/control raw native evidence and same candidate executable','five documentation relative-link checks','stable evidence readback and no owned candidate process')
    executable=$candidateExe;executableSha256=Get-ReleaseHash $candidateExe;zipSha256=Get-ReleaseHash $zips[0].FullName;ownedCandidateProcesses=0;productionDirectoryUpdated=$false
}
$json = $audit | ConvertTo-Json -Depth 40
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine)
# CreateNew also closes the existence-check race; an earlier audit is never overwritten.
$stream = [IO.FileStream]::new($out, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
$readback = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
True $readback.passed 'Written audit readback failed.'
[ordered]@{passed=$readback.passed;audit=$out;testsPassed=$total;sourceInputs=$inputs.Count;sourceLocks=$locks.Count;productionChanges=$changes.Count;csvGroups=$csvGroupCount;controlGroups=$controlCount;timedSamples=$sampleCount;excludedCsvVerifications=$verificationCount;controlDigests=$controlCount;uiGroups=$uiSeen.Count;uiProcesses=$processes.Count;nativeSearchCases=$caseSeen.Count;nativeControlScenarios=$controlSeen.Count;checks=$audit.checks} | ConvertTo-Json -Depth 6
