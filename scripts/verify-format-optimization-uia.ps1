#requires -Version 7.0
<#
.SYNOPSIS
Verifies all eight original fixture formats in the real packaged GUI through owned-PID UIA.
.EXAMPLE
pwsh -NoProfile -File scripts/verify-format-optimization-uia.ps1 -Executable artifacts/PACKAGE/DocSearch.exe -FixtureToolDirectory tools/LocalDocSearch.Fixtures/bin/Release/net10.0
.DESCRIPTION
-FixtureToolDirectory must contain the already-built LocalDocSearch.Fixtures.exe,
LocalDocSearch.Fixtures.dll and its runtime dependencies. The script never builds.
The tool is called with exactly one new, empty corpus directory: Program.Main calls
SampleDocuments.CreateAll and creates its eight original documents there. No existing
user corpus is accepted or copied. -UiaRuntimeRoot optionally selects an installed
WindowsDesktop runtime directory containing UIAutomationClient.dll/Types.dll.
Results are written to a new artifacts/format-optimization-uia-GUID/results.json.
Schema v1: passed, packageBefore/After, fixtureTool, fixtureGeneration, ownedProcessId,
coreClrPath/coreClrFromPackage, sourceBefore/After/sourceFilesUnchanged, cases[].
Each case contains the same whole-corpus query, strict counters, and matches[] with
per-file expected/actual path, entire preview text, source display and selection
evidence. A missing TextPattern is explicitly recorded; ValuePattern read-back and
application-reported SelectedText status are never called independent UIA selection.
Actual search diagnostics supply errors/warnings/suppressed counts and four real
Path/Code/Message scope-issue rows. Per-preview RawViewWalker ancestor evidence
requires four unique preview IDs and the original seed runtime ID; filename/source
lookups stay inside that independent pane and exclude result/search/main ancestors.
Only native UIA patterns drive the owned app. No clipboard, global input, external
reader, Office automation, original-file opening, or forced process termination.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$FixtureToolDirectory,
    [string]$UiaRuntimeRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Permit this review copy under artifacts as well as the promoted scripts path.
if (-not (Test-Path -LiteralPath (Join-Path $workspace 'src/LocalDocSearch.Core/LocalDocSearch.Core.csproj') -PathType Leaf)) {
    $workspace = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
}
if (-not (Test-Path -LiteralPath (Join-Path $workspace 'src/LocalDocSearch.Core/LocalDocSearch.Core.csproj') -PathType Leaf)) { throw 'Expected a workspace-local scripts or artifacts review-draft location.' }
function Resolve-WorkspacePath([string]$Path, [string]$Label) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (-not $resolved.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw ($Label + ' must be inside this workspace.')
    }
    return $resolved
}
$exe = Resolve-WorkspacePath $Executable 'Executable'
$package = Split-Path $exe
$fixtureDirectory = Resolve-WorkspacePath $FixtureToolDirectory 'FixtureToolDirectory'
$fixtureExe = Join-Path $fixtureDirectory 'LocalDocSearch.Fixtures.exe'
$fixtureDll = Join-Path $fixtureDirectory 'LocalDocSearch.Fixtures.dll'
$packageFiles = @($exe) + @('DocSearch.dll', 'LocalDocSearch.Core.dll', 'LocalDocSearch.Extractors.dll', 'coreclr.dll' | ForEach-Object { Join-Path $package $_ })
foreach ($required in ($packageFiles + @($fixtureExe, $fixtureDll, (Join-Path $fixtureDirectory 'LocalDocSearch.Fixtures.runtimeconfig.json'), (Join-Path $fixtureDirectory 'LocalDocSearch.Fixtures.deps.json')))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Required already-built file missing: ' + $required) }
}
if ($UiaRuntimeRoot) { $uiaRoot = (Resolve-Path -LiteralPath $UiaRuntimeRoot).Path }
else {
    $framework = Join-Path $env:ProgramFiles 'dotnet/shared/Microsoft.WindowsDesktop.App'
    $available = @(Get-ChildItem -LiteralPath $framework -Directory | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'UIAutomationClient.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'UIAutomationTypes.dll') -PathType Leaf)
    } | Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending | Select-Object -First 1 -ExpandProperty FullName)
    if ($available.Count -ne 1) { throw 'Specify -UiaRuntimeRoot containing UIAutomationClient.dll and UIAutomationTypes.dll.' }
    $uiaRoot = $available[0]
}
foreach ($assembly in @('UIAutomationTypes.dll', 'UIAutomationClient.dll')) { Add-Type -Path (Join-Path $uiaRoot $assembly) }

# Independent expected literals from SampleDocuments.cs/PdfFixture.cs, cross-checked
# against SampleIntegrationTests and the SourcePositionFormatter UI contract.
# PPTX nonvisual shape id=3 is named 正文; its source display uses that name.
$fixtures = @(
    [ordered]@{ name = '中文示例.txt'; format = 'TXT'; document = '入库记录：西瓜'; offset = 5; source = '行 1 · 列 6'; warningCount = 0; locator = @{ kind = 'TextLine'; line = 1; column = 6 } },
    [ordered]@{ name = '中文示例.md'; format = 'MD'; document = '# 原创记录：西瓜'; offset = 7; source = '行 1 · 列 8'; warningCount = 0; locator = @{ kind = 'TextLine'; line = 1; column = 8 } },
    [ordered]@{ name = '中文示例.log'; format = 'LOG'; document = '[INFO] 入库：西瓜'; offset = 10; source = '行 1 · 列 11'; warningCount = 0; locator = @{ kind = 'TextLine'; line = 1; column = 11 } },
    [ordered]@{ name = '跨行字段.csv'; format = 'CSV'; document = "第一行`n西瓜汁，附注""原样"",保留逗号；.*"; offset = 4; source = '记录 2 · 字段列 2 · 字段起始行 2'; warningCount = 0; locator = @{ kind = 'CsvField'; record = 2; field = 2; line = 2 } },
    [ordered]@{ name = '正文与表格.docx'; format = 'DOCX'; document = '原创正文：西瓜'; offset = 5; source = '正文段落 1（结构编号）'; warningCount = 1; locator = @{ kind = 'WordParagraph'; paragraph = 1 } },
    [ordered]@{ name = '单元格定位.xlsx'; format = 'XLSX'; document = '西瓜'; offset = 0; source = '工作表 Sheet2 · B15（共享字符串）'; warningCount = 1; locator = @{ kind = 'ExcelCell'; sheet = 'Sheet2'; address = 'B15'; row = 15; cell = 2; valueKind = '共享字符串' } },
    [ordered]@{ name = '八张幻灯片.pptx'; format = 'PPTX'; document = '西瓜'; offset = 0; source = '幻灯片 8 · 对象 正文 · 段落 1'; warningCount = 1; locator = @{ kind = 'SlideText'; slide = 8; objectName = '正文'; paragraph = 1 } },
    [ordered]@{ name = '三页文字.pdf'; format = 'PDF'; document = '第三页：西瓜'; offset = 4; source = 'PDF 页码 3（页内提取文本）'; warningCount = 1; locator = @{ kind = 'PdfPage'; page = 3 } }
)
# These messages are independent expected literals from the four extractor sources.
# Actual issue path/code/message are read separately from the search diagnostic UI.
$scopeMessages = [ordered]@{
    '正文与表格.docx' = 'Word 位置为正文结构提示，并非页码；页眉页脚、批注、文本框及修订删除文字不搜索。'
    '单元格定位.xlsx' = 'Excel 仅搜索可见普通工作表；值为文件保存的原始值或公式缓存，不重算公式，也不代表 Excel 格式化显示值；批注、图表等不搜索。'
    '八张幻灯片.pptx' = 'PowerPoint 仅搜索幻灯片顶层文本框及表格；备注、组合图形、母版、图表和 SmartArt 不搜索。'
    '三页文字.pdf' = 'PDF 按文件内容顺序提取每页文字；多栏、定位绘制文字的阅读顺序可能与视觉不同，OCR 未启用。'
}
$term = '西瓜'
foreach ($fixture in $fixtures) {
    if ($fixture.document.IndexOf($term, [StringComparison]::Ordinal) -ne $fixture.offset -or
        $fixture.document.LastIndexOf($term, [StringComparison]::Ordinal) -ne $fixture.offset) { throw ('Invalid independent fixture expectation: ' + $fixture.name) }
}
$owned = Join-Path $workspace ('artifacts/format-optimization-uia-' + [guid]::NewGuid().ToString('N'))
$corpus = Join-Path $owned 'corpus'
New-Item -ItemType Directory -Path $owned, $corpus | Out-Null
$settings = Join-Path $owned 'settings.json'
[IO.File]::WriteAllText($settings, '{"Version":1,"UseAnd":false,"Extensions":[".txt",".md",".log",".csv",".docx",".xlsx",".pptx",".pdf"],"AllowOfficeAutomation":false}', [Text.UTF8Encoding]::new($false))
function File-States([string[]]$Paths) {
    @($Paths | Sort-Object | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [ordered]@{ path = $item.FullName; length = $item.Length; mtimeUtcTicks = $item.LastWriteTimeUtc.Ticks; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    })
}
function Source-State { File-States @(Get-ChildItem -LiteralPath $corpus -File -Recurse | Select-Object -ExpandProperty FullName) }
$packageBefore = @(File-States $packageFiles)
$before = @(); $beforeJson = $null
$desktop = [Windows.Automation.AutomationElement]::RootElement
$desc = [Windows.Automation.TreeScope]::Descendants
$owner = $null; $main = $null; $generator = $null
$case = $null
$realizations = [Collections.Generic.List[object]]::new()
$matchChecks = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    schemaVersion = 1; passed = $false; checkedAtUtc = [datetime]::UtcNow.ToString('O')
    executable = $exe; scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    packageBefore = $packageBefore; packageAfter = @(); packageFilesUnchanged = $false
    fixtureTool = @(File-States @($fixtureExe, $fixtureDll)); fixtureToolDirectory = $fixtureDirectory
    fixtureGeneration = @{ method = 'Already-built Fixture EXE, one output-directory argument; Program.Main -> SampleDocuments.CreateAll'; outputDirectory = $corpus; processId = $null; exitCode = $null; files = @(); passed = $false }
    testOnlyUiaRuntime = $uiaRoot; reportPath = Join-Path $owned 'results.json'; settingsPath = $settings
    ownedProcessId = $null; coreClrPath = $null; coreClrFromPackage = $false
    ownedProcessClosed = $false; normalCloseRequested = $false
    sourceBefore = @(); sourceAfter = @(); sourceFilesUnchanged = $false
    clipboardTouched = $false; globalInputUsed = $false; originalFileOpened = $false; officeAutomationEnabled = $false
    cases = @(); realizations = @(); error = $null; cleanupErrors = @()
    capabilityBoundary = 'ValuePattern verifies the entire read-only preview; PreviewSelectionStatus reports application-verified SelectedText. Only supported TextPattern supplies independent native selection evidence. Source locator fields are expected literals; actual source evidence is the exact displayed source, not introspection of model objects.'
    notes = 'One real GUI search of eight new original files, each with one 西瓜 hit. Four normal Office/PDF scope warnings make the expected terminal state Partial, with zero skipped files. Search duration is not a benchmark. No performance conclusion is inferred.'
}
function Assert-Owner($Element) {
    if ($null -eq $owner -or $null -eq $Element -or $Element.Current.ProcessId -ne $owner.Id) { throw 'Owned UIA element missing or belongs to another process.' }
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
function By-Id($Root, [string]$Id) {
    Assert-Owner $Root
    $element = $Root.FindFirst($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
    if ($null -ne $element) { Assert-Owner $element; return $element }
}
function By-Name($Root, [string]$Name) {
    Assert-Owner $Root
    $element = $Root.FindFirst($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name))
    if ($null -ne $element) { Assert-Owner $element; return $element }
}
function Owned-Windows {
    @($desktop.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $owner.Id)))
}
function Owned-Id([string]$Id) {
    foreach ($window in (Owned-Windows)) {
        Assert-Owner $window
        if ($window.Current.AutomationId -ceq $Id) { return $window }
        $element = By-Id $window $Id
        if ($null -ne $element) { return $element }
    }
}
function Invoke-Element($Element) {
    Assert-Owner $Element
    if (-not $Element.Current.IsEnabled) { throw 'Owned control disabled.' }
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Value($Element, [string]$Text) { Assert-Owner $Element; $Element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text) }
function Read-Value($Element) { Assert-Owner $Element; $Element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Select-Element($Element) { Assert-Owner $Element; $Element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Ancestor-Window($Element) {
    Assert-Owner $Element
    while ($Element.Current.ControlType -ne [Windows.Automation.ControlType]::Window) {
        $Element = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($Element)
        Assert-Owner $Element
    }
    return $Element
}
function Runtime-Key($Element) {
    Assert-Owner $Element
    return [string]::Join(',', [int[]]$Element.GetRuntimeId())
}
function Element-Evidence($Element) {
    Assert-Owner $Element
    $rectangle = $Element.Current.BoundingRectangle
    $bounds = if ($rectangle.IsEmpty) { $null } else { @{ x = $rectangle.X; y = $rectangle.Y; width = $rectangle.Width; height = $rectangle.Height } }
    return [ordered]@{ runtimeId = [int[]]$Element.GetRuntimeId(); automationId = $Element.Current.AutomationId; name = $Element.Current.Name; controlType = $Element.Current.ControlType.ProgrammaticName; bounds = $bounds; processId = $Element.Current.ProcessId }
}
function Raw-Nodes($Root) {
    Assert-Owner $Root
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    $pending = [Collections.Generic.Stack[object]]::new()
    $nodes = [Collections.Generic.List[object]]::new()
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $node = $pending.Pop(); Assert-Owner $node
        if (-not $visited.Add((Runtime-Key $node))) { throw 'Raw UIA subtree contains duplicate runtime IDs.' }
        $nodes.Add($node)
        if ($nodes.Count -gt 4096) { throw 'Raw UIA subtree exceeded the bounded 4096-node inspection limit.' }
        $children = [Collections.Generic.List[object]]::new()
        $child = $walker.GetFirstChild($node)
        while ($null -ne $child) {
            Assert-Owner $child
            $children.Add($child)
            if ($children.Count -gt 4096) { throw 'Raw UIA sibling enumeration exceeded its bounded limit.' }
            $child = $walker.GetNextSibling($child)
        }
        for ($index = $children.Count - 1; $index -ge 0; $index--) { $pending.Push($children[$index]) }
    }
    return $nodes.ToArray()
}
function Raw-UniqueId($Root, [string]$Id) {
    $found = @(Raw-Nodes $Root | Where-Object { $_.Current.AutomationId -ceq $Id })
    if ($found.Count -gt 1) { throw ('Duplicate raw UIA automation ID within the scoped pane: ' + $Id) }
    if ($found.Count -eq 1) { return $found[0] }
}
function Raw-UniqueName($Root, [string]$Name) {
    $found = @(Raw-Nodes $Root | Where-Object { $_.Current.Name -ceq $Name })
    if ($found.Count -gt 1) { throw ('Duplicate raw UIA name within the scoped pane: ' + $Name) }
    if ($found.Count -eq 1) { return $found[0] }
}
function Preview-Pane($Seed, $Evidence) {
    Assert-Owner $Seed
    $Evidence.seed = Element-Evidence $Seed
    $Evidence.seedRuntimeId = [int[]]$Seed.GetRuntimeId()
    $seedKey = Runtime-Key $Seed
    $mainKey = Runtime-Key $main
    $requiredIds = @('PreviewTextBox', 'PreviewSelectionStatus', 'PreviewOpenOriginal', 'CloseInlinePreview')
    $candidate = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($Seed)
    for ($depth = 1; $depth -le 32 -and $null -ne $candidate; $depth++) {
        Assert-Owner $candidate
        $entry = [ordered]@{ depth = $depth; element = (Element-Evidence $candidate); idCounts = @{}; forbidden = @(); rejectedBecause = $null }
        $Evidence.ancestors += $entry
        if ((Runtime-Key $candidate) -ceq $mainKey) { $entry.rejectedBecause = 'Main window is excluded; no independent preview pane was found.'; break }
        $nodes = @(Raw-Nodes $candidate)
        $forbidden = @($nodes | Where-Object { $_.Current.AutomationId -ceq 'SearchResultsArea' -or $_.Current.Name -ceq '搜索根目录' -or $_.Current.Name -ceq '待添加关键词' -or (Runtime-Key $_) -ceq $mainKey })
        $entry.forbidden = @($forbidden | ForEach-Object { Element-Evidence $_ })
        if ($forbidden.Count -gt 0) { $entry.rejectedBecause = 'Ancestor includes a result/search/main-window element.'; break }
        $allUnique = $true; $scopedSeed = $null
        foreach ($id in $requiredIds) {
            $found = @($nodes | Where-Object { $_.Current.AutomationId -ceq $id })
            $entry.idCounts[$id] = $found.Count
            if ($found.Count -ne 1) { $allUnique = $false }
            elseif ($id -ceq 'PreviewTextBox') { $scopedSeed = $found[0] }
        }
        if ($allUnique) {
            $Evidence.scopedSeedRuntimeId = [int[]]$scopedSeed.GetRuntimeId()
            $Evidence.seedRuntimeIdMatches = (Runtime-Key $scopedSeed) -ceq $seedKey
            if (-not $Evidence.seedRuntimeIdMatches) { $entry.rejectedBecause = 'Unique PreviewTextBox does not match the original seed runtime ID.'; throw $entry.rejectedBecause }
            $Evidence.pane = Element-Evidence $candidate
            $Evidence.uniqueControlIds = $entry.idCounts
            $Evidence.passed = $true
            return $candidate
        }
        $entry.rejectedBecause = 'This closer raw ancestor does not contain each of the four required IDs exactly once.'
        $candidate = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($candidate)
    }
    $Evidence.error = 'No independent raw preview pane satisfies the four unique controls and original seed runtime ID; main-window fallback is excluded.'
    throw $Evidence.error
}
function Apply-Query {
    Invoke-Element (By-Id $main 'OpenQueryGrouping')
    $editor = Wait-Until { Owned-Id 'QueryGroupingWindow' } 'query editor'
    Select-Element (By-Id $editor 'AdvancedLogicTab')
    $combo = By-Id $editor 'QueryEditingMode'; Assert-Owner $combo
    $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Select-Element (Wait-Until { By-Name $editor '手写公式' } 'manual query mode')
    $box = Wait-Until { By-Id $editor 'QueryManualFormula' } 'manual query textbox'
    Set-Value $box $term
    if ((Read-Value $box) -cne $term) { throw 'Manual query ValuePattern read-back differs.' }
    Wait-Until { if ((Read-Value (By-Id $editor 'QueryGroupFormulaPreview')) -ceq $term -and (By-Id $editor 'QueryGroupApply').Current.IsEnabled) { $true } } 'query formula preview' | Out-Null
    Invoke-Element (By-Id $editor 'QueryGroupApply')
    Wait-Until { if ($null -eq (Owned-Id 'QueryGroupingWindow')) { $true } } 'query committed' | Out-Null
    $formula = (By-Id $main 'SearchQueryFormula').Current.Name
    if ($formula -cne ('公式：' + $term)) { throw ('Committed query differs: ' + $formula) }
    return $formula
}
function Visible-FileRows([string]$Name) {
    $area = By-Id $main 'SearchResultsArea'; Assert-Owner $area
    @($area.FindAll($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)) | Where-Object {
        Assert-Owner $_
        $_.Current.Name.StartsWith($Name + '  ·  ', [StringComparison]::Ordinal) -or $null -ne (By-Name $_ $Name)
    })
}
function Result-Scroll {
    $area = By-Id $main 'SearchResultsArea'; Assert-Owner $area
    foreach ($element in (@($area) + @($area.FindAll($desc, [Windows.Automation.Condition]::TrueCondition)))) {
        Assert-Owner $element
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern) -and $pattern.Current.VerticallyScrollable) { return $pattern }
    }
}
function File-Row([string]$Name) {
    $scroll = $null; $steps = 0; $reset = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $rows = @(Visible-FileRows $Name)
        if ($rows.Count -gt 1) { throw ('Duplicate realized file row: ' + $Name) }
        if ($rows.Count -eq 1) {
            $row = $rows[0]; Assert-Owner $row
            $item = $null
            if ($row.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$item)) { $item.ScrollIntoView() }
            $realizations.Add([ordered]@{ file = $Name; scrollSteps = $steps; resetToTop = $reset; scrollItemSupported = ($null -ne $item) })
            return $row
        }
        if ($null -eq $scroll) { $scroll = Result-Scroll }
        if ($null -eq $scroll) { throw ('File row missing and no owned vertical ScrollPattern is available: ' + $Name) }
        if (-not $reset) { $scroll.SetScrollPercent(-1, 0); $reset = $true }
        elseif ($scroll.Current.VerticalScrollPercent -lt 100) { $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::SmallIncrement); $steps++ }
        else { break }
        Start-Sleep -Milliseconds 50
    }
    throw ('File row not realized after scrolling: ' + $Name)
}
function Set-Expanded($Fixture, [bool]$Expanded) {
    $row = File-Row $Fixture.name
    foreach ($node in (@($row) + @($row.FindAll($desc, [Windows.Automation.Condition]::TrueCondition)))) {
        Assert-Owner $node
        $pattern = $null
        if ($node.Current.Name.Contains($Fixture.name) -and $node.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
            if ($Expanded) {
                if ($pattern.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Collapsed) { throw 'Fresh file result was not collapsed.' }
                $pattern.Expand()
            } else { $pattern.Collapse() }
            return 'ExpandCollapsePattern'
        }
    }
    $toggle = By-Name $row '展开或收起文件匹配'; Assert-Owner $toggle
    $pattern = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $target = if ($Expanded) { [Windows.Automation.ToggleState]::On } else { [Windows.Automation.ToggleState]::Off }
    if ($Expanded -and $pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { throw 'Fresh header toggle was not collapsed.' }
    if ($pattern.Current.ToggleState -ne $target) { $pattern.Toggle() }
    return 'TogglePattern'
}
function Owned-Name([string]$Name) {
    foreach ($window in (Owned-Windows)) { $element = By-Name $window $Name; if ($null -ne $element) { return $element } }
}
function Read-SearchDiagnostics {
    # Read the aggregate SEARCH diagnostics, never preview warning counts.
    Invoke-Element (By-Name $main '说明与诊断')
    $diagnosticsElement = Wait-Until { Owned-Id 'SearchDiagnosticsText' } 'actual search diagnostics'
    $diagnostics = $diagnosticsElement.Current.Name
    $case.actual.searchDiagnostics = $diagnostics
    if (($diagnostics -replace ',', '') -notmatch '^本次 [0-9]+ ms · 错误 ([0-9]+) · 警告 ([0-9]+) · 省略详情 ([0-9]+) · 最长界面刷新间隔 [0-9]+ ms$') { throw ('Search diagnostics format differs: ' + $diagnostics) }
    $case.actual.scopeErrors = [int]$Matches[1]
    $case.actual.scopeWarnings = [int]$Matches[2]
    $case.actual.scopeSuppressed = [int]$Matches[3]
    if ($case.actual.scopeErrors -ne 0 -or $case.actual.scopeWarnings -ne 4 -or $case.actual.scopeSuppressed -ne 0) { throw ('Actual search diagnostics must report errors 0, warnings 4, suppressed 0: ' + $diagnostics) }
    Invoke-Element (By-Name $main '搜索状态与问题详情')
    $list = Wait-Until { Owned-Name '搜索诊断详情列表' } 'actual search issue detail list'
    Assert-Owner $list
    $case.actual.scopeIssueList = Element-Evidence $list
    $seen = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    Wait-Until { if ($list.FindAll($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)).Count -gt 0) { $true } } 'realized diagnostic row' | Out-Null
    $scroll = $null
    # Templated Avalonia ScrollViewer peers are Raw-view elements; the
    # control-view descendant query omits their actual ScrollPattern.
    foreach ($node in (Raw-Nodes $list)) {
        Assert-Owner $node
        $candidate = $null
        if ($node.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$candidate) -and $candidate.Current.VerticallyScrollable) {
            $scroll = $candidate
            $case.actual.scopeIssueScrollProvider = Element-Evidence $node
            break
        }
    }
    if ($null -ne $scroll) { $scroll.SetScrollPercent(-1, 0) }
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $rows = @($list.FindAll($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
        $scanPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($row in $rows) {
            Assert-Owner $row
            $texts = @($row.FindAll($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
            foreach ($textNode in $texts) { Assert-Owner $textNode }
            if ($texts.Count -ne 3) { throw 'Diagnostic row must expose exactly its real Path, Code and Message text nodes; Severity is not exposed by this template.' }
            $issue = [ordered]@{ path = $texts[0].Current.Name; code = $texts[1].Current.Name; message = $texts[2].Current.Name; row = (Element-Evidence $row); pathNode = (Element-Evidence $texts[0]); codeNode = (Element-Evidence $texts[1]); messageNode = (Element-Evidence $texts[2]); severityExposedByRow = $false }
            if (-not $scanPaths.Add($issue.path)) { throw ('Duplicate simultaneously realized diagnostic path: ' + $issue.path) }
            if ($seen.ContainsKey($issue.path)) {
                $previous = $seen[$issue.path]
                if ($previous.code -cne $issue.code -or $previous.message -cne $issue.message) { throw 'One diagnostic path exposed conflicting code/message values while scrolling.' }
            } else { $seen.Add($issue.path, $issue) }
            $case.actual.scopeIssues = @($seen.Values | Sort-Object { $_.path })
            $fileName = [IO.Path]::GetFileName($issue.path)
            if (-not $scopeMessages.Contains($fileName) -or $issue.path -cne (Join-Path $corpus $fileName) -or $issue.code -cne 'Unknown' -or $issue.message -cne $scopeMessages[$fileName]) { throw ('Unexpected actual search scope issue: ' + $issue.path + ' / ' + $issue.code + ' / ' + $issue.message) }
        }
        $percent = if ($null -eq $scroll) { $null } else { $scroll.Current.VerticalScrollPercent }
        $case.actual.scopeIssueTraversal += [ordered]@{ attempt = $attempt; realizedRows = $rows.Count; uniquePathsRead = $seen.Count; verticalScrollPercent = $percent }
        if ($null -eq $scroll -or $percent -ge 100) {
            if ($seen.Count -eq 4) { break }
            if ($null -eq $scroll) {
                # Avalonia's popup ListBox exposes ScrollItem on realized rows even
                # when its list/descendants expose no vertical ScrollPattern.
                $lastRealized = @($rows | Sort-Object { $_.Current.BoundingRectangle.Bottom })[-1]
                $itemScroll = $null
                if (-not $lastRealized.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$itemScroll)) { throw 'Diagnostic list exposes neither vertical scrolling nor realized-row ScrollItemPattern.' }
                $case.actual.scopeIssueTraversal[-1].fallbackScrollItem = Element-Evidence $lastRealized
                $itemScroll.ScrollIntoView()
            }
        } else { $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::SmallIncrement) }
        Start-Sleep -Milliseconds 50
    }
    if ($seen.Count -ne 4) { throw ('Expected four actual distinct search scope issue paths; read ' + $seen.Count) }
    $case.actual.scopeIssuesVerified = $true
}
function Close-Preview($Pane) {
    Assert-Owner $Pane
    $text = Raw-UniqueId $Pane 'PreviewTextBox'; Assert-Owner $text
    $window = Ancestor-Window $text
    if ($window.Current.NativeWindowHandle -eq $main.Current.NativeWindowHandle) { Invoke-Element (Raw-UniqueId $Pane 'CloseInlinePreview') }
    else { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
    Wait-Until { if ($null -eq (Owned-Id 'PreviewTextBox')) { $true } } 'preview closed' | Out-Null
}
function Verify-Match($Fixture) {
    $expectedPath = Join-Path $corpus $Fixture.name
    $expectedStatus = '已选中 2 个字符 · ' + $Fixture.source
    if ($Fixture.warningCount -gt 0) { $expectedStatus += ' · 提取提示 ' + $Fixture.warningCount + ' 条' }
    $check = [ordered]@{
        file = $Fixture.name; format = $Fixture.format; passed = $false
        expected = @{ path = $expectedPath; source = $Fixture.source; locator = $Fixture.locator; entirePreviewText = $Fixture.document; term = $term; offsetUtf16 = $Fixture.offset; selectionLength = 2; selectionStatus = $expectedStatus; readOnly = $true; fileMatches = 1 }
        actual = @{ resultFilePath = $null; previewFileName = $null; matchSourceDisplay = $null; previewSourceDisplay = $null; entirePreviewText = $null; selectionStatus = $null; readOnly = $null; fileMatchesText = $null; matchRowSelected = $false; previewHost = $null; previewPane = @{ scopedToPreviewPane = $false; previewTextBoxFound = $false; previewSourceFound = $false; previewFileNameFound = $false; selectionStatusFound = $false; excludedSearchResultsArea = $false } }
        expansion = @{ method = $null; loadedText = $null; matchRows = $null; collapsedAfterChecks = $false }
        selectionEvidence = @{ nativeTextPatternSupported = $false; independentSelectionVerified = $false; independentlySelectedText = $null; applicationSelectedTextStatusVerified = $false; boundary = $null }
        previewPaneEvidence = @{ passed = $false; seed = $null; seedRuntimeId = @(); scopedSeedRuntimeId = @(); seedRuntimeIdMatches = $false; pane = $null; uniqueControlIds = @{}; ancestors = @(); fileNameNode = $null; sourceNode = $null; error = $null }
        error = $null
    }
    $matchChecks.Add($check)
    try {
        $row = File-Row $Fixture.name
        $pathText = By-Name $row $expectedPath; Assert-Owner $pathText
        $check.actual.resultFilePath = $pathText.Current.Name
        $countText = By-Name $row '1 处匹配'; Assert-Owner $countText
        $check.actual.fileMatchesText = $countText.Current.Name
        $check.expansion.method = Set-Expanded $Fixture $true
        $details = Wait-Until { By-Name (File-Row $Fixture.name) '匹配详情列表' } ('expanded match details ' + $Fixture.name)
        $loaded = Wait-Until { By-Name (File-Row $Fixture.name) '已展示 1 / 1 处' } ('loaded match ' + $Fixture.name)
        $check.expansion.loadedText = $loaded.Current.Name
        $position = $Fixture.source + ' · 关键词：' + $term
        # Expanded item containers can be realized after the footer is published.
        # Retry the exact row and scroll the owned detail/result provider as needed.
        $matchRow = Wait-Until {
            $rows = @($details.FindAll($desc, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
            if ($rows.Count -gt 1) { throw ('Expected one expanded match row: ' + $Fixture.name) }
            if ($rows.Count -eq 1 -and $null -ne (By-Name $rows[0] $position)) {
                Assert-Owner $rows[0]
                $item = $null
                if ($rows[0].TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$item)) { $item.ScrollIntoView() }
                return $rows[0]
            }
            $scroll = $null
            if (-not $details.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -or -not $scroll.Current.VerticallyScrollable) { $scroll = Result-Scroll }
            if ($null -ne $scroll -and $scroll.Current.VerticalScrollPercent -lt 100) { $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::SmallIncrement) }
        } ('exact realized detail row ' + $Fixture.name)
        $check.expansion.matchRows = 1
        Assert-Owner $matchRow
        $positionText = By-Name $matchRow $position; Assert-Owner $positionText
        $check.actual.matchSourceDisplay = $positionText.Current.Name
        Select-Element $matchRow
        $selected = $matchRow.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
        if (-not $selected) { throw 'Match row selection read-back differs.' }
        $check.actual.matchRowSelected = $selected
        # Scoped to this exact detail row; never the file header or external-reader action.
        Invoke-Element (By-Name $matchRow '定位并选中')
        $seed = Wait-Until { Owned-Id 'PreviewTextBox' } ('read-only preview ' + $Fixture.name)
        $pane = Preview-Pane $seed $check.previewPaneEvidence
        $text = Raw-UniqueId $pane 'PreviewTextBox'; Assert-Owner $text
        if ((Runtime-Key $text) -cne (Runtime-Key $seed)) { throw 'Preview seed runtime ID changed after resolving the independent pane.' }
        $window = Ancestor-Window $text
        $check.actual.previewHost = if ($window.Current.NativeWindowHandle -eq $main.Current.NativeWindowHandle) { 'inline' } else { 'window' }
        $status = Wait-Until { $element = Raw-UniqueId $pane 'PreviewSelectionStatus'; if ($null -ne $element -and $element.Current.Name -ceq $expectedStatus) { $element.Current.Name } } ('pane-scoped exact SelectedText status ' + $Fixture.name)
        $check.actual.selectionStatus = $status
        $check.selectionEvidence.applicationSelectedTextStatusVerified = $true
        $value = $text.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        $check.actual.readOnly = $value.Current.IsReadOnly
        $check.actual.entirePreviewText = $value.Current.Value
        if (-not $value.Current.IsReadOnly -or $value.Current.Value -cne $Fixture.document) { throw ('Entire read-only preview differs: ' + $Fixture.name) }
        $source = Wait-Until { Raw-UniqueName $pane $Fixture.source } ('unique pane-scoped exact preview source ' + $Fixture.name)
        $fileName = Wait-Until { Raw-UniqueName $pane $Fixture.name } ('unique pane-scoped preview filename ' + $Fixture.name)
        $check.actual.previewSourceDisplay = $source.Current.Name
        $check.actual.previewFileName = $fileName.Current.Name
        $check.previewPaneEvidence.sourceNode = Element-Evidence $source
        $check.previewPaneEvidence.fileNameNode = Element-Evidence $fileName
        # Set the audit summary only after the strict raw-pane/seed checks and
        # unique, exact pane-local name/source/status lookups above have passed.
        $check.actual.previewPane = @{ scopedToPreviewPane = $true; previewTextBoxFound = $true; previewSourceFound = $true; previewFileNameFound = $true; selectionStatusFound = $true; excludedSearchResultsArea = $true }
        $native = $null
        $supported = $text.TryGetCurrentPattern([Windows.Automation.TextPattern]::Pattern, [ref]$native)
        $check.selectionEvidence.nativeTextPatternSupported = $supported
        if ($supported) {
            $ranges = $native.GetSelection()
            if ($ranges.Count -ne 1) { throw 'Expected exactly one native UIA selection range.' }
            $selectedText = $ranges[0].GetText(-1)
            $check.selectionEvidence.independentlySelectedText = $selectedText
            if ($selectedText -cne $term) { throw 'Independent native UIA selected text differs.' }
            $check.selectionEvidence.independentSelectionVerified = $true
            $check.selectionEvidence.boundary = 'Independent TextPattern selected text plus entire ValuePattern document and application-reported SelectedText status. This fixture has exactly one occurrence, at the independently specified UTF-16 offset.'
        } else {
            $check.selectionEvidence.boundary = 'TextPattern unavailable: no independent native selection evidence. Entire read-only ValuePattern document, exact source display and application-verified SelectedText status passed. The expected document has exactly one occurrence.'
        }
        Close-Preview $pane
        $null = Set-Expanded $Fixture $false
        Wait-Until { if ($null -eq (By-Name (File-Row $Fixture.name) '匹配详情列表')) { $true } } ('collapsed match details ' + $Fixture.name) | Out-Null
        $check.expansion.collapsedAfterChecks = $true
        $check.passed = $true
    } catch { $check.error = $_.Exception.Message; throw }
}

try {
    if (@(Get-ChildItem -LiteralPath $corpus -Force).Count -ne 0) { throw 'Owned corpus must be empty before fixture generation.' }
    $generator = Start-Process -FilePath $fixtureExe -ArgumentList @('"' + $corpus + '"') -WorkingDirectory $fixtureDirectory -PassThru -RedirectStandardOutput (Join-Path $owned 'fixture-stdout.log') -RedirectStandardError (Join-Path $owned 'fixture-stderr.log')
    $report.fixtureGeneration.processId = $generator.Id
    if (-not $generator.WaitForExit(60000)) { throw 'Owned fixture generator did not finish within 60 seconds; no forced termination attempted.' }
    $generator.Refresh(); $report.fixtureGeneration.exitCode = $generator.ExitCode
    if ($generator.ExitCode -ne 0) { throw ('Fixture generator failed; see isolated fixture-stderr.log. Exit code: ' + $generator.ExitCode) }
    $generated = @(Get-ChildItem -LiteralPath $corpus -File -Recurse | Sort-Object Name)
    $report.fixtureGeneration.files = @($generated | Select-Object -ExpandProperty Name)
    if ($generated.Count -ne 8 -or @(Get-ChildItem -LiteralPath $corpus -Directory -Recurse).Count -ne 0 -or @($generated | Where-Object { $_.Name -cnotin @($fixtures.name) }).Count -ne 0) { throw 'Fixture generator did not produce exactly the eight expected original files.' }
    $report.fixtureGeneration.passed = $true
    $before = @(Source-State); $report.sourceBefore = $before
    $beforeJson = ConvertTo-Json -InputObject $before -Depth 6 -Compress
    $owner = Start-Process -FilePath $exe -ArgumentList @('--settings', ('"' + $settings + '"')) -WorkingDirectory $package -PassThru -RedirectStandardOutput (Join-Path $owned 'app-stdout.log') -RedirectStandardError (Join-Path $owned 'app-stderr.log')
    $report.ownedProcessId = $owner.Id
    $main = Wait-Until { foreach ($window in (Owned-Windows)) { if ($null -ne (By-Name $window '搜索根目录')) { return $window } } } 'owned main window'
    Assert-Owner $main
    $owner.Refresh(); $modules = @($owner.Modules | Where-Object ModuleName -eq 'coreclr.dll')
    if ($modules.Count -ne 1) { throw 'Expected exactly one loaded CoreCLR module.' }
    $report.coreClrPath = $modules[0].FileName
    $report.coreClrFromPackage = [string]::Equals($report.coreClrPath, (Join-Path $package 'coreclr.dll'), [StringComparison]::OrdinalIgnoreCase)
    if (-not $report.coreClrFromPackage) { throw 'Application did not load CoreCLR from its own package.' }
    $formula = Apply-Query
    Set-Value (By-Name $main '搜索根目录') $corpus
    if ((Read-Value (By-Name $main '搜索根目录')) -cne $corpus) { throw 'Search root ValuePattern read-back differs.' }
    $case = [ordered]@{ name = 'all-eight-formats-original-position-read-only-preview'; passed = $false; root = $corpus; query = $term; formula = $formula; expected = @{ scannedFiles = 8; publishedFiles = 8; matches = 8; skippedFiles = 0; terminalStatusPrefix = '部分完成'; scopeWarnings = 4; scopeErrors = 0; scopeSuppressed = 0; scopeIssues = @($scopeMessages.Keys | ForEach-Object { @{ path = (Join-Path $corpus $_); code = 'Unknown'; message = $scopeMessages[$_] } }); perFileMatches = 1; previewCount = 8 }; actual = $null; matches = @(); error = $null }
    Invoke-Element (By-Name $main '开始搜索')
    Wait-Until {
        $status = (By-Id $main 'SearchStatusText').Current.Name
        if ((By-Name $main '开始搜索').Current.IsEnabled -and ($status -like '部分完成*' -or $status -like '已完成*')) { $true }
    } 'whole-corpus search completion' 90 | Out-Null
    $progress = (By-Id $main 'SearchProgressText').Current.Name
    $status = (By-Id $main 'SearchStatusText').Current.Name
    if (($progress -replace ',', '') -notmatch '^已扫描 ([0-9]+) · 已展示文件 ([0-9]+) · 已展示匹配 ([0-9]+) · 跳过/失败 ([0-9]+)$') { throw ('Missing final counters: ' + $progress) }
    $scanned = [int]$Matches[1]; $files = [int]$Matches[2]; $hits = [int]$Matches[3]; $skipped = [int]$Matches[4]
    $case.actual = @{ scannedFiles = $scanned; publishedFiles = $files; matches = $hits; skippedFiles = $skipped; status = $status; progress = $progress; scopeWarnings = $null; scopeErrors = $null; scopeSuppressed = $null; searchDiagnostics = $null; scopeIssues = @(); scopeIssueList = $null; scopeIssueTraversal = @(); scopeIssuesVerified = $false }
    if ($scanned -ne 8 -or $files -ne 8 -or $hits -ne 8 -or $skipped -ne 0 -or $status -notlike '部分完成*') { throw ('Unexpected all-format outcome: ' + $progress + ' / ' + $status) }
    foreach ($fixture in $fixtures) { Verify-Match $fixture }
    if ($matchChecks.Count -ne 8 -or @($matchChecks | Where-Object { -not $_.passed }).Count -ne 0 -or @($matchChecks.format | Sort-Object -Unique).Count -ne 8) { throw 'Eight distinct, successful format preview checks are required.' }
    Read-SearchDiagnostics
    $case.passed = $true
    $report.passed = $true
}
catch {
    $report.error = $_.Exception.Message
    if ($null -ne $case) { $case.error = $report.error }
}
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
        } catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    try {
        $after = @(Source-State); $report.sourceAfter = $after
        $report.sourceFilesUnchanged = $null -ne $beforeJson -and (ConvertTo-Json -InputObject $after -Depth 6 -Compress) -ceq $beforeJson
        if (-not $report.sourceFilesUnchanged) { $cleanupErrors.Add('Source SHA256, length or mtime changed, or generation snapshot was not established.') }
        $packageAfter = @(File-States $packageFiles); $report.packageAfter = $packageAfter
        $report.packageFilesUnchanged = (ConvertTo-Json -InputObject $packageAfter -Depth 6 -Compress) -ceq (ConvertTo-Json -InputObject $packageBefore -Depth 6 -Compress)
        if (-not $report.packageFilesUnchanged) { $cleanupErrors.Add('Package EXE, application/Core/Extractors DLL or CoreCLR changed during verification.') }
    } catch { $cleanupErrors.Add($_.Exception.Message) }
    if (-not $report.ownedProcessClosed -or -not $report.normalCloseRequested) { $cleanupErrors.Add('Normal owned process shutdown was not verified.') }
    if ($null -ne $generator -and -not $generator.HasExited) { $cleanupErrors.Add('Owned fixture generator is still running; no forced termination attempted.') }
    $report.cleanupErrors = $cleanupErrors.ToArray()
    if ($cleanupErrors.Count -gt 0) { $report.passed = $false; if ($null -eq $report.error) { $report.error = $cleanupErrors -join ' | ' } }
    if ($null -ne $case) { $case.matches = $matchChecks.ToArray(); $report.cases = @($case) }
    $report.realizations = $realizations.ToArray()
    $report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $report.reportPath -Encoding utf8NoBOM
    $report | ConvertTo-Json -Depth 16
}
if (-not $report.passed) { throw $report.error }
