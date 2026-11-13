[CmdletBinding()]
param(
    [string]$BenchmarkDll = (Join-Path $PSScriptRoot '../tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/LocalDocSearch.Benchmarks.dll'),
    [string]$DotnetPath = 'dotnet',
    [string]$CorpusRoot = (Join-Path $PSScriptRoot 'phase-4-corpus-standard-64')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Invocation only: the integrating parent must restore/build once before running this script.
$BenchmarkDll = (Resolve-Path -LiteralPath $BenchmarkDll).Path
$CorpusRoot = (Resolve-Path -LiteralPath $CorpusRoot).Path
$dotnetCommand = (Get-Command $DotnetPath -ErrorAction Stop).Source
$logRoot = Join-Path $PSScriptRoot ('concurrency-experiment-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $logRoot
$baseline = @{}
$samples = [System.Collections.Generic.List[object]]::new()
$controls = [System.Collections.Generic.List[object]]::new()
$correctness = [System.Collections.Generic.List[object]]::new()
$script:binarySha = $null
$script:runtime = $null
$script:sourceSha = $null
$assemblyStamps = @(Get-ChildItem -LiteralPath (Split-Path $BenchmarkDll) -Filter 'LocalDocSearch.*.dll' -File |
    Sort-Object Name | ForEach-Object { [pscustomobject]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })

function Assert-Value([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw $Code }
}
function Assert-Sha([string]$Hash) { Assert-Value ($Hash -cmatch '^[A-F0-9]{64}$') 'invalid-sha256' }
function Invoke-Benchmark([string]$Command, [string]$Scenario, [int]$Workers, [string]$Label, [int]$Repeat = 1) {
    $jsonPath = Join-Path $logRoot ($Label + '.json')
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnetCommand
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($BenchmarkDll, $Command, $CorpusRoot, $Scenario, '--workers', "$Workers", '--output', $jsonPath)) {
        $start.ArgumentList.Add($argument)
    }
    if ($Command -eq 'run') { $start.ArgumentList.Add('--repeat'); $start.ArgumentList.Add("$Repeat") }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Value ($process.Start()) 'process-start-failed'
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'benchmark-process-timeout'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText((Join-Path $logRoot ($Label + '.stdout.log')), $stdout)
        [System.IO.File]::WriteAllText((Join-Path $logRoot ($Label + '.stderr.log')), $stderr)
        Assert-Value ($process.ExitCode -eq 0) "benchmark-process-failed:$Label"
        Assert-Value (Test-Path -LiteralPath $jsonPath -PathType Leaf) 'report-missing'
        $report = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
        Assert-Value ($report.passed -eq $true) "report-failed:$Label"
        Assert-Value ($report.workers -eq $Workers) 'report-workers'
        Assert-Value ($report.scenario -eq $Scenario) 'report-scenario'
        Assert-Value ($report.schemaVersion -eq 1) 'report-schema'
        Assert-Sha $report.binarySha256
        if ($null -eq $script:binarySha) { $script:binarySha = $report.binarySha256 }
        Assert-Value ($script:binarySha -ceq $report.binarySha256) 'binary-changed-between-processes'
        if ($Command -eq 'run') {
            Assert-Value ($report.integrity.verifiedBeforeAndAfter -eq $true) 'source-integrity'
            $sourceBefore = $report.integrity.sourceSha256Before
            $sourceAfter = $report.integrity.sourceSha256After
            if ($null -eq $script:runtime) { $script:runtime = $report.machine.runtime }
            Assert-Value ($script:runtime -ceq $report.machine.runtime) 'runtime-changed'
            Assert-Value (@($report.rounds).Count -eq $Repeat) 'round-count'
            $verification = $report.resultVerification
            Assert-Value ($verification.passed -eq $true -and $verification.phase -ceq 'verification-excluded') 'result-verification'
            Assert-Value (@($verification.mismatches).Count -eq 0) 'verification-mismatches'
            Assert-Value ($verification.status -ceq $report.expected.status) 'verification-status'
            Assert-Value ($verification.publishedFiles -eq $report.expected.matchedFiles -and $verification.publishedOccurrences -eq $report.expected.occurrences) 'verification-counts'
            Assert-Sha $verification.resultOrderSha256
            Assert-Sha $verification.resultContentSha256
            if (-not $baseline.ContainsKey($Scenario)) { $baseline[$Scenario] = @($verification.resultOrderSha256, $verification.resultContentSha256) }
            Assert-Value ($baseline[$Scenario][0] -ceq $verification.resultOrderSha256) "result-order-diverged:$Scenario"
            Assert-Value ($baseline[$Scenario][1] -ceq $verification.resultContentSha256) "result-content-diverged:$Scenario"
        } else {
            Assert-Value ($report.sourceIntegrity -eq $true) 'control-source-integrity'
            $sourceBefore = $report.sourceSha256Before
            $sourceAfter = $report.sourceSha256After
            Assert-Value (@($report.rounds).Count -eq 3) 'control-round-count'
        }
        Assert-Sha $sourceBefore
        Assert-Value ($sourceBefore -ceq $sourceAfter) 'source-before-after'
        if ($null -eq $script:sourceSha) { $script:sourceSha = $sourceBefore }
        Assert-Value ($script:sourceSha -ceq $sourceBefore) 'source-changed-between-processes'
        foreach ($round in $report.rounds) {
            Assert-Value ($round.passed -eq $true -and @($round.mismatches).Count -eq 0) 'round-failed'
            Assert-Value ($round.memory.totalAllocatedBytesDelta -ge 0) 'negative-allocation'
            Assert-Value ($round.memory.processPeakWorkingSetBytesLifetime -gt 0) 'missing-lifetime-peak'
            if ($Command -eq 'run') {
                Assert-Value ($round.status -ceq $report.expected.status) 'status'
                Assert-Value ($round.publishedFiles -eq $report.expected.matchedFiles -and $round.matchedFiles -eq $report.expected.matchedFiles) 'matched-files'
                Assert-Value ($round.publishedOccurrences -eq $report.expected.occurrences -and $round.occurrences -eq $report.expected.occurrences) 'occurrences'
                Assert-Value ($round.errorCount -eq $report.expected.errors -and $round.warningCount -eq $report.expected.warnings) 'diagnostics'
                Assert-Value ($round.firstResultMilliseconds -ge 0) 'first-result'
            } else {
                Assert-Sha $round.resultOrderSha256
                Assert-Sha $round.resultContentSha256
                Assert-Value ($round.requested -eq $true -and $round.searchTaskEnded -eq $true) 'control-request-or-task'
                if ($round.mode -ne 'running-stop') {
                    Assert-Value ($round.pauseAcknowledged -eq $true -and $round.countersFrozen -eq $true) 'control-pause'
                    Assert-Value ($round.freezeMilliseconds -ge 200 -and $round.pauseAcknowledgementMilliseconds -ge 0) 'control-freeze-duration'
                }
                if ($round.mode -eq 'pause-resume') {
                    Assert-Value ($round.resultOrderSha256 -ceq $baseline[$Scenario][0] -and $round.resultContentSha256 -ceq $baseline[$Scenario][1]) 'resume-result-digest'
                } else {
                    Assert-Value ($round.status -ceq 'Cancelled' -and $round.stopMilliseconds -ge 0) 'control-stop'
                }
            }
        }
        return $report
    } finally { $process.Dispose() }
}
function Get-Distribution($Values) {
    $ordered = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    $n = $ordered.Count
    Assert-Value ($n -gt 0) 'empty-distribution'
    $middle = [int][math]::Floor($n / 2)
    $median = if ($n % 2 -eq 0) { ($ordered[$middle - 1] + $ordered[$middle]) / 2 } else { $ordered[$middle] }
    return [pscustomobject]@{ count = $n; minimum = $ordered[0]; median = $median; maximum = $ordered[$n - 1] }
}
$sequence = @(1, 2, 4, 4, 2, 1)
$scenarios = @('many-small', 'large-log', 'mixed', 'dense-file', 'global-budget')
foreach ($scenario in $scenarios) {
    for ($group = 0; $group -lt $sequence.Count; $group++) {
        $workers = $sequence[$group]
        $report = Invoke-Benchmark 'run' $scenario $workers ("timing-$scenario-g$group-w$workers") 3
        foreach ($round in $report.rounds) {
            $samples.Add([pscustomobject]@{ scenario = $scenario; workers = $workers; group = $group; round = $round })
        }
    }
}
foreach ($scenario in @('errors', 'literal-and', 'cancel')) {
    foreach ($workers in @(1, 2, 4)) {
        $report = Invoke-Benchmark 'run' $scenario $workers ("correctness-$scenario-w$workers") 1
        $correctness.Add([pscustomobject]@{ scenario = $scenario; workers = $workers; passed = $report.passed })
    }
}
foreach ($scenario in @('many-small', 'mixed')) {
    foreach ($workers in @(1, 2, 4)) {
        $report = Invoke-Benchmark 'control' $scenario $workers ("control-$scenario-w$workers")
        foreach ($round in $report.rounds) { $controls.Add([pscustomobject]@{ scenario = $scenario; workers = $workers; measurement = $round }) }
    }
}
$summary = foreach ($scenario in $scenarios) {
    foreach ($workers in @(1, 2, 4)) {
        $rounds = @($samples | Where-Object { $_.scenario -eq $scenario -and $_.workers -eq $workers } | ForEach-Object { $_.round })
        Assert-Value ($rounds.Count -eq 6) 'six-samples-per-worker-required'
        [pscustomobject]@{
            scenario = $scenario; workers = $workers
            elapsedMilliseconds = Get-Distribution ($rounds | ForEach-Object { $_.elapsedMilliseconds })
            firstResultMilliseconds = Get-Distribution ($rounds | ForEach-Object { $_.firstResultMilliseconds })
            cpuMilliseconds = Get-Distribution ($rounds | ForEach-Object { $_.cpuMilliseconds })
            totalAllocatedBytesDelta = Get-Distribution ($rounds | ForEach-Object { $_.memory.totalAllocatedBytesDelta })
            lifetimePeakWorkingSetBytes = Get-Distribution ($rounds | ForEach-Object { $_.memory.processPeakWorkingSetBytesLifetime })
            gc0Delta = Get-Distribution ($rounds | ForEach-Object { $_.memory.collectionsDelta.generation0 })
            gc1Delta = Get-Distribution ($rounds | ForEach-Object { $_.memory.collectionsDelta.generation1 })
            gc2Delta = Get-Distribution ($rounds | ForEach-Object { $_.memory.collectionsDelta.generation2 })
        }
    }
}
# Verify the binary/dependency snapshot again after every process has exited.
foreach ($stamp in $assemblyStamps) {
    $actual = (Get-FileHash -LiteralPath (Join-Path (Split-Path $BenchmarkDll) $stamp.name) -Algorithm SHA256).Hash
    Assert-Value ($actual -ceq $stamp.sha256) 'assembly-changed-during-experiment'
}
$final = [pscustomobject]@{
    schemaVersion = 1; passed = $true; workersSequence = $sequence; repeatsPerFreshProcess = 3
    runtime = $script:runtime; binarySha256 = $script:binarySha; assemblies = $assemblyStamps
    sourceSha256 = $script:sourceSha; measurements = @($summary); controls = @($controls); correctness = @($correctness)
    cacheSemantics = 'OS cache not cleared; preflight SHA reads warm files; alternating fresh processes are sequential'
    peakSemantics = 'process lifetime cumulative high-water mark; repeats share a process; not per-search peak'
    digestSemantics = 'actual FileResult fields and every MatchItem field; relative paths exist only inside SHA inputs'
    interpretation = 'large-log has one eligible file: no file-parallel speedup expected; retain neutral or negative measurements'
}
$summaryPath = Join-Path $logRoot 'summary.json'
[System.IO.File]::WriteAllText($summaryPath, ($final | ConvertTo-Json -Depth 30))
$summary | Format-Table scenario, workers, @{Label='median ms'; Expression={$_.elapsedMilliseconds.median}}, @{Label='min ms'; Expression={$_.elapsedMilliseconds.minimum}}, @{Label='max ms'; Expression={$_.elapsedMilliseconds.maximum}}, @{Label='alloc median'; Expression={$_.totalAllocatedBytesDelta.median}}, @{Label='lifetime peak max'; Expression={$_.lifetimePeakWorkingSetBytes.maximum}} -AutoSize
Write-Output "Summary: $summaryPath"
