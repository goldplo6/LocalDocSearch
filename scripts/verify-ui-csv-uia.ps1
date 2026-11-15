param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$UiaRuntimeRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Standalone behavioral probe. No product hooks, external readers, clipboard or global input.
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = (Resolve-Path -LiteralPath $ExePath).Path
if (-not $exe.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Expected a package executable inside this workspace.' }
$package = Split-Path $exe
$dll = Join-Path $package 'DocSearch.dll'
$coreClr = Join-Path $package 'coreclr.dll'
foreach ($required in @($dll, $coreClr)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Self-contained package file missing: ' + $required) } }
if ($UiaRuntimeRoot) { $uiaRoot = (Resolve-Path -LiteralPath $UiaRuntimeRoot).Path }
else {
    $framework = Join-Path $env:ProgramFiles 'dotnet/shared/Microsoft.WindowsDesktop.App'
    $uiaRoot = @(Get-ChildItem -LiteralPath $framework -Directory | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'UIAutomationClient.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'UIAutomationTypes.dll') -PathType Leaf)
    } | Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending | Select-Object -First 1 -ExpandProperty FullName)
    if ($uiaRoot.Count -ne 1) { throw 'Specify -UiaRuntimeRoot containing UIAutomationClient.dll and UIAutomationTypes.dll.' }
    $uiaRoot = $uiaRoot[0]
}
foreach ($assembly in @('UIAutomationTypes.dll', 'UIAutomationClient.dll')) { Add-Type -Path (Join-Path $uiaRoot $assembly) }
$owned = Join-Path $workspace ('artifacts/ui-csv-uia-' + [guid]::NewGuid().ToString('N'))
$corpus = Join-Path $owned 'corpus'
$valid = Join-Path $corpus 'valid'
$broken = Join-Path $corpus 'broken'
New-Item -ItemType Directory -Path $valid, $broken | Out-Null
$settings = Join-Path $owned 'settings.json'
[IO.File]::WriteAllText($settings, '{"Version":1,"UseAnd":false,"Extensions":[".csv"],"AllowOfficeAutomation":false}', [Text.UTF8Encoding]::new($false))
# Independent literals, not an oracle computed by the implementation under test.
# CSV line numbers count normalized CRLF and single CR, including newlines inside quotes.
$fixtures = @(
    [ordered]@{
        name = 'valid-crlf.csv'; expectedHits = 5
        raw = "id,fruit,note`r`n" + '1,"西瓜,苹果","他说""西瓜"""' + "`r`n" + '2,"上半' + "`r`n" + '苹果","尾部西瓜"' + "`r`n"
        matches = @(
            @{ record = 2; field = 2; line = 2; term = '西瓜'; fieldText = '西瓜,苹果' },
            @{ record = 2; field = 2; line = 2; term = '苹果'; fieldText = '西瓜,苹果' },
            @{ record = 2; field = 3; line = 2; term = '西瓜'; fieldText = '他说"西瓜"' },
            @{ record = 3; field = 2; line = 3; term = '苹果'; fieldText = "上半`n苹果" },
            @{ record = 3; field = 3; line = 4; term = '西瓜'; fieldText = '尾部西瓜' }
        )
    },
    [ordered]@{
        name = 'valid-cr.csv'; expectedHits = 4
        raw = "kind,value`r" + 'A,"苹果' + "`r" + '西瓜"' + "`r" + 'B,"逗号,西瓜","苹果""派"""' + "`r"
        matches = @(
            @{ record = 2; field = 2; line = 2; term = '苹果'; fieldText = "苹果`n西瓜" },
            @{ record = 2; field = 2; line = 2; term = '西瓜'; fieldText = "苹果`n西瓜" },
            @{ record = 3; field = 2; line = 4; term = '西瓜'; fieldText = '逗号,西瓜' },
            @{ record = 3; field = 3; line = 4; term = '苹果'; fieldText = '苹果"派"' }
        )
    }
)
foreach ($fixture in $fixtures) { [IO.File]::WriteAllText((Join-Path $valid $fixture.name), $fixture.raw, [Text.UTF8Encoding]::new($false)) }
$brokenName = 'broken-late-quote.csv'
[IO.File]::WriteAllText((Join-Path $broken $brokenName), "id,value`r`n1,西瓜`r`n" + '2,"未闭合苹果', [Text.UTF8Encoding]::new($false))
$expectedIssue = '读取失败：CSV 格式错误：记录 3、列 2 的引号字段未闭合。'
function Source-State {
    @(Get-ChildItem -LiteralPath $corpus -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($corpus, $_.FullName); length = $_.Length; mtimeUtcTicks = $_.LastWriteTimeUtc.Ticks; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$before = @(Source-State)
$beforeJson = ConvertTo-Json -InputObject $before -Depth 6 -Compress
$desktop = [Windows.Automation.AutomationElement]::RootElement
$desc = [Windows.Automation.TreeScope]::Descendants
$owner = $null; $main = $null
$cases = [Collections.Generic.List[object]]::new()
$previews = [Collections.Generic.List[object]]::new()
$expansions = [Collections.Generic.List[object]]::new()
function Assert-Owner($element) {
    if ($null -eq $owner -or $null -eq $element -or $element.Current.ProcessId -ne $owner.Id) { throw 'Owned UIA element missing or belongs to another process.' }
}
function Wait-Until([scriptblock]$Probe, [string]$Label, [int]$Seconds = 30) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        if ($null -ne $owner -and $owner.HasExited) { throw ('Owned process exited: ' + $Label) }
        $value = & $Probe
        if ($value) { return $value }
        Start-Sleep -Milliseconds 50
    }
    throw ('UI condition timed out: ' + $Label)
}
function By-Id($root, [string]$id) {
    Assert-Owner $root
    $element = $root.FindFirst($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
    if ($null -ne $element) { Assert-Owner $element; return $element }
}
function By-Name($root, [string]$name) {
    Assert-Owner $root
    $element = $root.FindFirst($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name))
    if ($null -ne $element) { Assert-Owner $element; return $element }
}
function Owned-Windows {
    @($desktop.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $owner.Id)))
}
function Owned-Id([string]$id) {
    foreach ($window in (Owned-Windows)) {
        Assert-Owner $window
        if ($window.Current.AutomationId -ceq $id) { return $window }
        $element = By-Id $window $id
        if ($null -ne $element) { Assert-Owner $element; return $element }
    }
}
function Invoke-Element($element) {
    Assert-Owner $element
    if (-not $element.Current.IsEnabled) { throw 'Owned control disabled.' }
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Value($element, [string]$text) { Assert-Owner $element; $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Read-Value($element) { Assert-Owner $element; $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Select-Element($element) { Assert-Owner $element; $element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Ancestor-Window($element) {
    Assert-Owner $element
    while ($element.Current.ControlType -ne [Windows.Automation.ControlType]::Window) {
        $element = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($element)
        Assert-Owner $element
    }
    return $element
}
function Apply-Query {
    $query = '西瓜 OR 苹果'
    Invoke-Element (By-Id $main 'OpenQueryGrouping')
    $editor = Wait-Until { Owned-Id 'QueryGroupingWindow' } 'query editor'
    Select-Element (By-Id $editor 'AdvancedLogicTab')
    (By-Id $editor 'QueryEditingMode').GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Select-Element (Wait-Until { By-Name $editor '手写公式' } 'manual mode')
    $box = By-Id $editor 'QueryManualFormula'
    Set-Value $box $query
    if ((Read-Value $box) -cne $query) { throw 'Query ValuePattern read-back differs.' }
    Wait-Until { if ((Read-Value (By-Id $editor 'QueryGroupFormulaPreview')) -ceq $query -and (By-Id $editor 'QueryGroupApply').Current.IsEnabled) { $true } } 'query formula preview' | Out-Null
    Invoke-Element (By-Id $editor 'QueryGroupApply')
    Wait-Until { if ($null -eq (Owned-Id 'QueryGroupingWindow')) { $true } } 'query committed' | Out-Null
    $formula = (By-Id $main 'SearchQueryFormula').Current.Name
    if ($formula -cne ('公式：' + $query)) { throw ('Main query differs: ' + $formula) }
    return $formula
}
function File-Rows {
    $area = By-Id $main 'SearchResultsArea'
    Assert-Owner $area
    @($area.FindAll($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)) | Where-Object {
        $row = $_
        @($fixtures | Where-Object { $row.Current.Name.Contains($_.name) -or $null -ne (By-Name $row $_.name) }).Count -gt 0 -or $row.Current.Name.Contains($brokenName)
    })
}
function File-Row([string]$name) {
    $rows = @(File-Rows | Where-Object { $_.Current.Name.Contains($name) -or $null -ne (By-Name $_ $name) })
    if ($rows.Count -ne 1) { throw ('Expected exactly one visible file row: ' + $name + '; actual=' + $rows.Count) }
    Assert-Owner $rows[0]
    return $rows[0]
}
function Search-Case([string]$label, [string]$root, [int]$skippedExpected, [bool]$partial) {
    Set-Value (By-Name $main '搜索根目录') $root
    if ((Read-Value (By-Name $main '搜索根目录')) -cne $root) { throw 'Search root read-back differs.' }
    Invoke-Element (By-Name $main '开始搜索')
    $expectedTerminal = if ($partial) { '部分完成*' } else { '已完成*' }
    Wait-Until {
        $status = (By-Id $main 'SearchStatusText').Current.Name
        if ((By-Name $main '开始搜索').Current.IsEnabled -and $status -like $expectedTerminal) { $true }
    } ('search ' + $label) | Out-Null
    $progress = (By-Id $main 'SearchProgressText').Current.Name
    $status = (By-Id $main 'SearchStatusText').Current.Name
    if (($progress -replace ',', '') -notmatch '^已扫描 ([0-9]+) · 已展示文件 ([0-9]+) · 已展示匹配 ([0-9]+) · 跳过/失败 ([0-9]+)$') { throw ('Missing final counters: ' + $progress) }
    $scanned = [int]$Matches[1]; $files = [int]$Matches[2]; $hits = [int]$Matches[3]; $skipped = [int]$Matches[4]
    if ($scanned -ne (2 + $skippedExpected) -or $files -ne 2 -or $hits -ne 9 -or $skipped -ne $skippedExpected -or ($partial -and $status -notlike '部分完成*') -or (-not $partial -and $status -notlike '已完成*')) { throw ('Unexpected search outcome: ' + $progress + ' / ' + $status) }
    Wait-Until { if (@(File-Rows).Count -eq 2) { $true } } ('two published file rows ' + $label) | Out-Null
    $published = @($fixtures | ForEach-Object {
        $fileRow = File-Row $_.name
        $countText = [string]$_.expectedHits + ' 处匹配'
        $countLabel = By-Name $fileRow $countText
        Assert-Owner $countLabel
        [ordered]@{ file = $_.name; hits = $_.expectedHits; countLabel = $countLabel.Current.Name }
    })
    $names = @($published | ForEach-Object { $_.file })
    if (@(File-Rows | Where-Object { $_.Current.Name.Contains($brokenName) -or $null -ne (By-Name $_ $brokenName) }).Count -ne 0) { throw 'Malformed CSV published a partial file result.' }
    $case = [ordered]@{ name = $label; root = $root; passed = $true; formula = (By-Id $main 'SearchQueryFormula').Current.Name; expected = @{ scannedFiles = 2 + $skippedExpected; publishedFiles = 2; hits = 9; skippedFiles = $skippedExpected; partial = $partial }; actual = @{ scannedFiles = $scanned; publishedFiles = $files; hits = $hits; skippedFiles = $skipped; status = $status; progress = $progress; filenames = $names; perFile = $published }; issue = $null }
    $cases.Add($case)
    return $case
}
function Expand-File($fixture) {
    $row = File-Row $fixture.name
    $nodes = @($row) + @($row.FindAll($desc, [Windows.Automation.Condition]::TrueCondition))
    $provider = $null; $method = $null
    foreach ($node in $nodes) {
        Assert-Owner $node
        if (-not $node.Current.Name.Contains($fixture.name)) { continue }
        $pattern = $null
        if ($node.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
            if ($pattern.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Collapsed) { throw 'Fresh result was not collapsed.' }
            $pattern.Expand(); $provider = $pattern; $method = 'ExpandCollapsePattern'; break
        }
    }
    if ($null -eq $provider) {
        # Some Avalonia peers expose only the header ToggleButton, inside the exact file row.
        $toggles = @($nodes | Where-Object { Assert-Owner $_; $candidate = $null; $_.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$candidate) })
        if ($toggles.Count -ne 1) { throw ('Expected one owned result header toggle: ' + $fixture.name) }
        $provider = $toggles[0].GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($provider.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { throw 'Fresh header toggle was not collapsed.' }
        $provider.Toggle(); $method = 'TogglePattern'
    }
    $details = Wait-Until { By-Name (File-Row $fixture.name) '匹配详情列表' } ('expanded details ' + $fixture.name)
    $loaded = '已展示 ' + $fixture.expectedHits + ' / ' + $fixture.expectedHits + ' 处'
    Wait-Until { By-Name (File-Row $fixture.name) $loaded } ('loaded footer ' + $fixture.name) | Out-Null
    $button = By-Name (File-Row $fixture.name) '再显示 50 处'
    Assert-Owner $button
    if ($button.Current.IsEnabled) { throw 'Final-page placeholder must be disabled.' }
    $expansions.Add([ordered]@{ file = $fixture.name; passed = $true; mechanism = $method; loadedText = $loaded; finalPageButtonPresent = $true; finalPageButtonOffscreen = $button.Current.IsOffscreen; finalPageButtonEnabled = $button.Current.IsEnabled; positions = @(); collapsedAfterChecks = $false })
    return $details
}
function Collapse-File([string]$file) {
    $row = File-Row $file
    $nodes = @($row) + @($row.FindAll($desc, [Windows.Automation.Condition]::TrueCondition))
    foreach ($node in $nodes) {
        Assert-Owner $node
        $pattern = $null
        if ($node.Current.Name.Contains($file) -and $node.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
            $pattern.Collapse(); return
        }
    }
    $toggles = @($nodes | Where-Object { Assert-Owner $_; $candidate = $null; $_.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$candidate) })
    if ($toggles.Count -ne 1) { throw ('Expected one owned result header toggle to collapse: ' + $file) }
    $pattern = $toggles[0].GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) { $pattern.Toggle() }
}
function Match-Row([string]$file, [string]$position) {
    $details = By-Name (File-Row $file) '匹配详情列表'
    Assert-Owner $details
    $scroll = $null
    $canScroll = $details.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll)
    if ($canScroll -and $scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0) }
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $rows = @($details.FindAll($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
        $found = @($rows | Where-Object { $null -ne (By-Name $_ $position) })
        if ($found.Count -eq 1) {
            Assert-Owner $found[0]
            $item = $null
            if ($found[0].TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$item)) { $item.ScrollIntoView() }
            return $found[0]
        }
        if ($found.Count -gt 1) { throw ('Duplicate match position: ' + $position) }
        if ($canScroll -and $scroll.Current.VerticallyScrollable -and $scroll.Current.VerticalScrollPercent -lt 100) { $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::SmallIncrement) }
        Start-Sleep -Milliseconds 50
    }
    throw ('Match row not realized: ' + $file + ' / ' + $position)
}
function Verify-Preview($fixture, $match, [string]$source, [string]$requestedHost = 'automatic') {
    $text = Wait-Until { Owned-Id 'PreviewTextBox' } 'read-only preview'
    $window = Ancestor-Window $text
    $hostKind = if ($window.Current.NativeWindowHandle -eq $main.Current.NativeWindowHandle) { 'inline' } else { 'window' }
    if ($requestedHost -ne 'automatic' -and $hostKind -cne $requestedHost) { throw 'Preview did not move to the requested host.' }
    $expectedStatus = '已选中 2 个字符 · ' + $source
    $status = Wait-Until { $element = Owned-Id 'PreviewSelectionStatus'; if ($null -ne $element -and $element.Current.Name -ceq $expectedStatus) { $element.Current.Name } } 'verified application selection status'
    $valuePattern = $text.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    if (-not $valuePattern.Current.IsReadOnly) { throw 'Preview ValuePattern is not read-only.' }
    $value = $valuePattern.Current.Value
    if ($value -cne $match.fieldText) { throw ('Preview extracted field text differs: ' + $fixture.name + ' / ' + $source) }
    $null = Wait-Until { By-Name $window $source } 'preview exact source feedback'
    $null = Wait-Until { By-Name $window $fixture.name } 'preview filename feedback'
    $pattern = $null; $selected = $null
    $native = $text.TryGetCurrentPattern([Windows.Automation.TextPattern]::Pattern, [ref]$pattern)
    $evidence = 'ValuePattern document read-back plus application-reported SelectedText verification; no independent UIA selection evidence.'
    if ($native) {
        $ranges = $pattern.GetSelection()
        if ($ranges.Count -ne 1 -or $ranges[0].GetText(-1) -cne $match.term) { throw 'Native UIA selected text differs.' }
        $selected = $ranges[0].GetText(-1)
        $evidence = 'Independent native UIA TextPattern selection plus ValuePattern read-back and application-reported SelectedText verification.'
    }
    $previews.Add([ordered]@{ file = $fixture.name; record = $match.record; field = $match.field; fieldStartLine = $match.line; term = $match.term; passed = $true; host = $hostKind; expectedDocument = $match.fieldText; documentValue = $value; readOnly = $valuePattern.Current.IsReadOnly; sourceFeedback = $source; selectionStatus = $status; nativeTextPatternSupported = $native; independentlySelectedText = $selected; evidence = $evidence })
    return $hostKind
}
function Close-Preview {
    $text = Owned-Id 'PreviewTextBox'
    if ($null -eq $text) { return }
    $window = Ancestor-Window $text
    if ($window.Current.NativeWindowHandle -eq $main.Current.NativeWindowHandle) { Invoke-Element (By-Id $main 'CloseInlinePreview') }
    else { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
    Wait-Until { if ($null -eq (Owned-Id 'PreviewTextBox')) { $true } } 'preview closed' | Out-Null
}
$expectedFiles = @($fixtures | ForEach-Object { [ordered]@{ file = $_.name; expectedHits = $_.expectedHits; expectedMatches = $_.matches } })
$report = [ordered]@{
    schemaVersion = 1; passed = $false; checkedAtUtc = [datetime]::UtcNow.ToString('O'); executable = $exe
    exeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash; docSearchDll = $dll; docSearchDllSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; testOnlyUiaRuntime = $uiaRoot
    reportPath = Join-Path $owned 'results.json'; settingsPath = $settings; ownedProcessId = $null; coreClrPath = $null; coreClrFromPackage = $false
    ownedProcessClosed = $false; normalCloseRequested = $false; sourceFilesUnchanged = $false; sourceBefore = $before; sourceAfter = @()
    clipboardTouched = $false; globalInputUsed = $false; originalFileOpened = $false
    expectedFiles = $expectedFiles; malformedFile = @{ file = $brokenName; earlyMatchingText = '西瓜'; expectedPublishedFile = $false; expectedIssueCode = 'InvalidData'; expectedIssue = $expectedIssue }
    cases = @(); expansions = @(); previews = @(); error = $null; cleanupErrors = @()
    notes = 'Only the owned PID is driven through UIA. Source fixtures and settings are isolated in this new artifact directory. ValuePattern and internal selection status are distinguished from independent TextPattern evidence. No timing/performance conclusion or internal wrapper count is inferred.'
}
try {
    $owner = Start-Process -FilePath $exe -ArgumentList @('--settings', ('"' + $settings + '"')) -WorkingDirectory $package -PassThru
    $report.ownedProcessId = $owner.Id
    $main = Wait-Until { foreach ($window in (Owned-Windows)) { if ($null -ne (By-Name $window '搜索根目录')) { return $window } } } 'owned main window'
    Assert-Owner $main
    $owner.Refresh()
    $modules = @($owner.Modules | Where-Object ModuleName -eq 'coreclr.dll')
    if ($modules.Count -ne 1) { throw 'Expected exactly one loaded CoreCLR module.' }
    $report.coreClrPath = $modules[0].FileName
    $report.coreClrFromPackage = [string]::Equals($report.coreClrPath, $coreClr, [StringComparison]::OrdinalIgnoreCase)
    if (-not $report.coreClrFromPackage) { throw 'Application did not load CoreCLR from its own package.' }
    $null = Apply-Query
    $null = Search-Case 'valid-csv-exact' $valid 0 $false
    $popOutChecked = $false
    foreach ($fixture in $fixtures) {
        $null = Expand-File $fixture
        $positions = [Collections.Generic.List[string]]::new()
        foreach ($match in $fixture.matches) {
            $source = '记录 ' + $match.record + ' · 字段列 ' + $match.field + ' · 字段起始行 ' + $match.line
            $position = $source + ' · 关键词：' + $match.term
            $row = Match-Row $fixture.name $position
            $positions.Add($position)
            Invoke-Element (By-Name $row '定位并选中')
            $hostKind = Verify-Preview $fixture $match $source
            if (-not $popOutChecked -and $hostKind -ceq 'inline') {
                Invoke-Element (Owned-Id 'PopOutPreview')
                $null = Wait-Until { $text = Owned-Id 'PreviewTextBox'; if ($null -ne $text -and (Ancestor-Window $text).Current.NativeWindowHandle -ne $main.Current.NativeWindowHandle) { $true } } 'standalone preview window'
                $null = Verify-Preview $fixture $match $source 'window'
                $popOutChecked = $true
            }
            Close-Preview
        }
        $expansions[$expansions.Count - 1].positions = $positions.ToArray()
        Collapse-File $fixture.name
        $expansions[$expansions.Count - 1].collapsedAfterChecks = $true
    }
    $partialCase = Search-Case 'late-malformed-csv-is-atomic' $corpus 1 $true
    Invoke-Element (By-Name $main '搜索状态与问题详情')
    $issues = Wait-Until { foreach ($window in (Owned-Windows)) { $element = By-Name $window '搜索诊断详情列表'; if ($null -ne $element) { return $element } } } 'issue detail list'
    $null = Wait-Until { By-Name $issues (Join-Path $broken $brokenName) } 'malformed file issue path'
    $issueMessage = Wait-Until { By-Name $issues $expectedIssue } 'precise unclosed quote issue'
    $issueCode = Wait-Until { By-Name $issues 'InvalidData' } 'invalid CSV issue code'
    $partialCase.issue = [ordered]@{ path = Join-Path $broken $brokenName; message = $issueMessage.Current.Name; code = $issueCode.Current.Name; matchedEarlyButFileUnpublished = $true }
    $report.passed = $true
}
catch { $report.error = $_.Exception.Message }
finally {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    if ($null -ne $owner) {
        try {
            $owner.Refresh()
            if (-not $owner.HasExited) {
                foreach ($window in (Owned-Windows)) {
                    Assert-Owner $window
                    if ($null -ne $main -and $window.Current.NativeWindowHandle -ne $main.Current.NativeWindowHandle) {
                        $pattern = $null
                        if ($window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) { $pattern.Close() }
                    }
                }
                $report.normalCloseRequested = $owner.CloseMainWindow()
                if (-not $owner.WaitForExit(10000)) { $cleanupErrors.Add('Owned application did not close normally within 10 seconds; no forced termination attempted.') }
            }
            $owner.Refresh(); $report.ownedProcessClosed = $owner.HasExited
        }
        catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    try {
        $after = @(Source-State); $report.sourceAfter = $after
        $report.sourceFilesUnchanged = (ConvertTo-Json -InputObject $after -Depth 6 -Compress) -ceq $beforeJson
        if (-not $report.sourceFilesUnchanged) { $cleanupErrors.Add('Source SHA256, length or mtime changed.') }
    }
    catch { $cleanupErrors.Add($_.Exception.Message) }
    if (-not $report.ownedProcessClosed -or -not $report.normalCloseRequested) { $cleanupErrors.Add('Normal owned process shutdown was not verified.') }
    $report.cleanupErrors = $cleanupErrors.ToArray()
    if ($cleanupErrors.Count -gt 0) { $report.passed = $false; if ($null -eq $report.error) { $report.error = $cleanupErrors -join ' | ' } }
    $report.cases = $cases.ToArray(); $report.expansions = $expansions.ToArray(); $report.previews = $previews.ToArray()
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report.reportPath -Encoding utf8NoBOM
    $report | ConvertTo-Json -Depth 12
}
if (-not $report.passed) { throw $report.error }
