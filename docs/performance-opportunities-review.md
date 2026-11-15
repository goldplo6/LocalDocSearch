# rc.10 后续提速可能性审查

日期：2026-11-14；2026-11-15 追加按格式审查。基线：`e7167a3`（rc.10）；工作区 rc.11 已包含 UI 首屏懒加载与 CSV 块解析（未提交）。状态：只做代码审查和现有测量复核；**未修改产品代码，未重新运行基准**。本文把“可尝试的改动”和“已确认的收益”分开，所有未实测的预期都按待验证处理。

## 摘要

- 查询匹配层已经过 rc.9／rc.10 两轮优化；`SearchValues` 延迟创建、Excel 地址 Span 解析、原列号来源复用均已落地，继续在匹配循环里抠小项不是当前最高收益。
- rc.11 已实施 **UI 首屏匹配懒加载** 和 **CSV 块解析**。按格式看，下一步收益最集中的是 **XLSX／DOCX 的 OOXML SAX 读取**，其次是 CSV 未加引号 span、文本直接解码、Excel 打包 key 和 PDF 缓冲消费后释放。
- 如果目标是“同一批文件反复搜索更快”，应单独评估**可失效的提取缓存**；如果目标是“第一次搜索也接近 Everything”，则需要 **n-gram 候选索引**。两者都不是微优化，不能与当前预筛混为一谈。
- Everything 默认依赖 NTFS 文件名／元数据索引，并不默认读取正文；本应用每次搜索仍要打开、解码和解析源文件，因此内容搜索不可能靠匹配层微优化追平文件名搜索。

## 当前实现地图

| 阶段 | 当前实现 | 相关位置 | 观察 |
|---|---|---|---|
| 目录发现 | 深度优先枚举；每个目录项一次 `File.GetAttributes`；过滤扩展名、临时文件和重解析点 | [TextSearchEngine.cs:209](../src/LocalDocSearch.Core/TextSearchEngine.cs#L209)、[TextSearchEngine.Concurrency.cs:225](../src/LocalDocSearch.Core/TextSearchEngine.Concurrency.cs#L225) | 非候选文件也要做一次属性查询；目录本身还会再次查询属性 |
| 文件快照 | 候选文件开始一次 `FileInfo.Refresh`，读取长度和修改时间 | [TextSearchEngine.cs:315](../src/LocalDocSearch.Core/TextSearchEngine.cs#L315) | `FileInfo` 的多个 getter 共享同一次刷新，不能按 getter 数量计算系统调用 |
| 提取 | TXT／MD／LOG 按行；CSV 按字段；Office／PDF 先完整缓冲 | [PlainTextExtractor.cs:57](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L57)、[CsvTextExtractor.cs:21](../src/LocalDocSearch.Core/CsvTextExtractor.cs#L21)、[ReadOnlyExtraction.cs:28](../src/LocalDocSearch.Extractors/ReadOnlyExtraction.cs#L28) | 纯文本每行固定分配多个对象；CSV 仍有逐字符异步链；Office／PDF 解析器本身的成本占主导 |
| 匹配 | 逐词 `IndexOf`；≥32 个不同词且片段≥256 字符时先用 `SearchValues` 求最早候选位置；正则以 `Match`／`NextMatch` 枚举 | [TextSearchEngine.cs:355](../src/LocalDocSearch.Core/TextSearchEngine.cs#L355)、[TextSearchEngine.cs:365](../src/LocalDocSearch.Core/TextSearchEngine.cs#L365)、[TextSearchEngine.cs:425](../src/LocalDocSearch.Core/TextSearchEngine.cs#L425) | 命中枚举、预算和完整 EOF 检查仍以原路径为准；不要为“提前返回”短路 |
| 文件发布 | 完整提取、公式资格和 EOF 后元数据复核都通过才发布整个文件 | [TextSearchEngine.cs:403](../src/LocalDocSearch.Core/TextSearchEngine.cs#L403) | 无命中文件也会做末次复核；这是当前诊断契约的一部分 |
| 界面交付 | 有界结果队列；每文件构造 `FileResultViewModel` 时立即加载 50 条 `MatchViewModel`；UI 线程 4 ms 时间片消费 | [MainWindowViewModel.cs:421](../src/LocalDocSearch.Desktop/ViewModels/MainWindowViewModel.cs#L421)、[FileResultViewModel.cs:18](../src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs#L18) | 大量命中时，折叠行也会创建包装对象并触发集合通知 |
| 预览 | 重新完整提取源文件，核对片段、偏移和上下文；全进程串行 | [MatchPreviewLoader.cs:17](../src/LocalDocSearch.Desktop/MatchPreviewLoader.cs#L17) | 点击预览的等待与搜索耗时分开计算，但用户感知会叠加 |

## 已有证据

| 主题 | 已有结果 | 解释 |
|---|---|---|
| 多关键词预筛 | 32 词无命中 12.74→11.54 ms（约 −9.4%），64 词无命中 17.03→13.73 ms（约 −19.4%），中文 64 词合成语料约 −86%～−92% | 只对长片段和较多词生效，不能外推到少词、Office／PDF 或所有中文文档 |
| 少词对照 | 4 词无命中 6.96→8.26 ms，16 词无命中 9.92→10.44 ms | 初版 4 词门槛回退，最终收紧到 32 词；没有必要继续降低门槛 |
| 固定开销 | Excel 坐标 50 万次 115.32→37.74 ms，坐标循环分配 162,381,344→0 B | 只证明坐标解析本身；不代表整份 XLSX 端到端同比加速 |
| 大日志 | 64 MiB 级日志耗时约 461.2→471.0 ms，累计分配约 276.1→275.6 MB | 分配下降不等于耗时下降；固定开销轮次仍有小幅负收益 |
| 多小文件分配 | rc.8 小文件累计分配减少约 82.4%，但耗时约 +0.8% | 短命对象减少没有自动转化为墙钟时间 |
| 多文件并发 | 5,000 文件实窗 W=4 约 3020.5→1933 ms（−36.0%），混合格式约 393.4→267.6 ms（−32.0%） | CPU 约 +22%～23%，工作集约 +7.6%；默认仍为 1 是有意的资源取舍 |
| 多小文件波动 | rc.10 初测多小文件约 +38.7%，紧邻交替复测约 +0.27% | 说明当前端到端数据存在明显波动，继续微调前必须先固定测量口径 |

依据：[literal-prefilter-results.md:24](literal-prefilter-results.md#L24)、[fixed-overhead-results.md:39](fixed-overhead-results.md#L39)、[multifile-search-results.md:46](multifile-search-results.md#L46)、[bounded-concurrency-results.md:7](bounded-concurrency-results.md#L7)。

## 可改点

### P0：UI 首屏匹配懒加载（rc.11 已实施）

**状态。** 工作区 [FileResultViewModel.cs:19](../src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs#L19) 已改为构造时不创建包装，首次展开才加载第一页；[ui-csv-optimization-results.md](ui-csv-optimization-results.md) 记录了回归与实窗对照。以下保留原始分析，供复核。

**现状。** [FileResultViewModel.cs:18](../src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs#L18) 的构造函数直接调用 `LoadMore()`，[FileResultViewModel.cs:49](../src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs#L49) 立即创建最多 50 个 `MatchViewModel` 并逐条加入 `ObservableCollection`。结果行即使处于折叠状态，这 50 个包装对象和集合通知也已经发生。结果由 [MainWindowViewModel.cs:421](../src/LocalDocSearch.Desktop/ViewModels/MainWindowViewModel.cs#L421) 在 UI 线程创建，每个文件都要占用 4 ms 结果时间片的一部分；结果队列容量只有 64，UI 处理慢会反向阻塞搜索工作者。

**可做。** 构造函数只保存 `FileResult`，不创建匹配包装；折叠行继续使用已有的 `FirstMatch`／`FirstBefore`／`FirstText`／`FirstAfter` 展示首条摘要。用户展开时再调用第一页加载，`Result_OnExpanding` 再执行滚动定位。`HasMore`／`LoadedText` 保留，展开后行为不变。

**预期。** 大量命中、较多匹配文件时，减少 UI 线程对象创建和集合变更通知，降低结果队列背压，缩短整轮交付时间；对单文件小结果影响很小。

**代价与风险。** 需要更新 Desktop 测试中“构造后立即有 50 条”的断言；需要验证展开、回收容器、滚动定位和“再显示 50 处”没有回归。这是本报告中最适合先做的一项，不涉及引擎结果语义。

### P1：发现阶段元数据合并

**现状。** 当前每个目录项至少发生一次 `File.GetAttributes`；候选文件开始再 `FileInfo.Refresh`，完整读取结束再 `Refresh`。候选文件实际有三次属性／元数据查询，非候选文件也会在发现阶段查询一次。此前 6,455 文件纯元数据探针中，每文件 3 次读取约 1.46–1.61 s，1 次约 0.51–0.57 s；这是纯元数据上限参考，不等于端到端同比。

**可做。** 用 `DirectoryInfo.EnumerateFileSystemInfos()` 返回的类型和属性完成目录／文件、重解析点和扩展名分类，省掉发现阶段那次独立属性查询；候选文件仍保留开始快照和 EOF 复核，即从 3 次降为 2 次。最大胆的版本是把枚举得到的长度／时间也当作初始快照，从而降到 1 次，但这会把“文件开始快照”提前到枚举时刻，改变进度回调、排队期间改源和删除／替换条目的诊断时序。

**为什么之前没有做。** [fixed-overhead-results.md:12](fixed-overhead-results.md#L12) 明确记录：缓存枚举属性可能早于前一文件回调，处理后来被删除或替换为 reparse 的条目时会改变提取与诊断时序；文档选择保留 fresh 分类、开始快照和无命中末检查。

**建议。** 如果重启这项优化，先只做“枚举信息用于分类”的保守版本，并保留 fresh 打开和末检查；不要把无命中文件的末次检查直接删掉，除非明确接受 `FileChanged` 诊断语义变化。Windows 上 .NET 的枚举结果通常已带 `FindData`，但需要用实际探针确认属性访问没有再触发系统调用；其它平台要单独处理。

### P1：CSV 块解析（rc.11 已实施）

**状态。** 工作区已保留 8192 字符缓冲、把逐字符异步调用改为同步 `TryRead`，只在缓冲耗尽时异步补充；旧算法差分与实窗对照见 [ui-csv-optimization-results.md](ui-csv-optimization-results.md)。仍可继续做的是未加引号字段的 span 快进，见下文“按文档格式”。

**现状。** [CsvTextExtractor.cs:21](../src/LocalDocSearch.Core/CsvTextExtractor.cs#L21) 的主循环逐个字符调用 `ReadAsync`，[CsvTextExtractor.cs:79](../src/LocalDocSearch.Core/CsvTextExtractor.cs#L79) 的 `CsvCharReader.ReadAsync` 再逐字符推进缓冲。磁盘读已经是 8 KiB 块，问题在于解析层的逐字符异步调用和逐字符取消检查。

**可做。** 保留现有的引号、转义、CRLF 规范化、字段编号和预算状态机，但改成“每次拿一个缓冲区块，尽量用 `ReadOnlySpan<char>` 推进”；只有跨缓冲区的半个字段才保留到 `StringBuilder`。按块检查取消，而不是按字符检查。

**预期与代价。** 对大型 CSV 可能有明显提取收益，但当前没有端到端占比数据，必须先计时。需要覆盖引号、CRLF／CR、多行字段、字段预算、取消和尾部记录；[fixed-overhead-results.md:13](fixed-overhead-results.md#L13) 已把这项列为另案测量。

### P1：Excel 剩余每单元格开销

**现状。** 地址识别已由 Regex 改为 Span 解析（[ExcelTextExtractor.cs:130](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L130)），但每个单元格仍会对地址做 `ToUpperInvariant()`（[ExcelTextExtractor.cs:68](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L68)），并使用字符串 `HashSet` 做重复地址检查（[ExcelTextExtractor.cs:55](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L55)）。大工作表上这会带来大量短期字符串和哈希开销。

**可做。** 用 `(row, column)` 的打包值做重复检查；地址大写延迟到真正需要写入来源或报错时。若输出契约要求大写地址，保留一次规范化字符串，但不要为空白单元格或重复检查额外创建。需要保留原来的缺失引用跳过、坐标语法、当前文化 `int.TryParse` 和错误输出。

**预期与代价。** 适合大 XLSX；收益应在整份工作簿端到端测量，不能拿坐标微基准外推。改动局部，风险主要为地址大小写／重复错误消息的既有断言。

### P2：纯文本逐行分配

**现状。** [PlainTextExtractor.cs:57](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L57) 和 [PlainTextExtractor.cs:70](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L70) 每一行都会创建行文本、`TextSegment`、`SourceLocation` 和 `line:N` 字符串；[SearchModels.cs:50](../src/LocalDocSearch.Core/SearchModels.cs#L50) 中这些模型是 class。64 MiB 级日志每轮累计分配约 274–276 MB，即使没有命中也会产生这些短命对象。

**可做。**

1. 低风险版本：延迟 `line:N` 标识或复用不可变来源对象，减少每行字符串／来源对象；需要同步调整预览的片段匹配和既有 ID 断言。
2. 高收益版本：为纯文本增加专用扫描路径，直接在字符缓冲区内寻找候选行，只对可能命中的行创建 `TextSegment`；这等于把候选判断前移到提取器，能同时减少行文本和包装对象，但属于独立设计，不能改变行号、编码错误、预算、暂停和完整 EOF 语义。

**注意。** rc.8 已经证明累计分配大幅下降不一定带来耗时下降（小文件分配 −82.4%，耗时约 +0.8%）。因此这条路线必须同时报告耗时、CPU、累计分配和峰值，不能只拿 GC 数字当成果。

### P2：自动编码模式的重复解码

**现状。** 默认 `StrictUnicode` 只做一次严格 UTF-8 解码；但当用户选择 `Utf8OrGb18030` 或显式 `Gb18030` 且文件无 BOM 时，[PlainTextExtractor.cs:113](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L113) 会先用 `StreamReader` 解码整个文件做预验证，失败后再用 GB18030 验证，之后还要重新读取提取。[PlainTextExtractor.cs:154](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L154) 的 `ValidateAsync` 是这次额外解码的来源。

**可做。** UTF-8 分支使用 .NET 10 的 `System.Text.Unicode.Utf8.IsValid(ReadOnlySpan<byte>)` 做字节级验证，只有失败时才走 GB18030 解码验证；保留“验证失败前不发布任何片段”的边界。GB18030 仍需要真正的解码器验证。

**预期与代价。** 条件性收益，只影响非默认编码模式；默认 `StrictUnicode` 用户不会感受到。改动小，但需要覆盖无效尾字节、BOM 优先、预取消和回退警告。

### P3：正则命中枚举

**现状。** [TextSearchEngine.cs:425](../src/LocalDocSearch.Core/TextSearchEngine.cs#L425) 的 `Match`／`NextMatch` 每个命中都会产生 `Match` 对象；正则模式在密集命中时会有额外分配和 GC 压力。

**可做。** 评估 `Regex.EnumerateMatches`（`ReadOnlySpan<char>` 重载）以减少命中对象分配；保留现有零长度跳过、累计 1000 ms 预算、单次 100 ms 超时和“超时不发布部分文件”的行为。

**预期与代价。** 只惠及正则用户，优先级低于文本和 UI；必须单独验证超时和零长度语义，不能直接用 `NextMatch` 的测试结论替代。

### P3：并发档位

**现状。** [TextSearchEngine.cs:47](../src/LocalDocSearch.Core/TextSearchEngine.cs#L47) 默认 `maxConcurrency` 为 1；2／4 是显式实验路径。[bounded-concurrency-results.md:7](bounded-concurrency-results.md#L7) 记录 5,000 文件窗口约 −36%、混合格式约 −32%，但 CPU 约 +22%～23%、工作集约 +7.6%。[TextSearchEngine.Concurrency.cs:58](../src/LocalDocSearch.Core/TextSearchEngine.Concurrency.cs#L58) 目前每文件创建一个 `Task` 和本地扫描状态；如果以后把并发作为正式功能，可考虑固定工作者循环减少每文件任务／状态分配。

**建议。** 这是最快的策略型提速，但不是免费：可以给高级设置提供 2 档或按格式／文件规模自适应，不建议在没有更多大型 Office／PDF 证据时无条件默认 4。若默认仍为 1，先不要把并发当作主要优化项。

### P4：Office／PDF 缓冲与预览

[ReadOnlyExtraction.cs:28](../src/LocalDocSearch.Extractors/ReadOnlyExtraction.cs#L28) 会先把整个文件提取到 `List<TextSegment>` 再交给引擎。引擎本来就要完整 EOF、公式资格和末检查后才发布，单纯改成流式不一定会更快，且可能让解析器 DOM 与搜索过程重叠、增加峰值内存；因此不建议把它当作第一项性能改动。真正的大头是重复搜索时的解析缓存，或首次搜索的候选索引。

预览会重新完整提取（[MatchPreviewLoader.cs:17](../src/LocalDocSearch.Desktop/MatchPreviewLoader.cs#L17)），且全进程串行；点击预览的等待不计入搜索耗时，但用户感知会叠加。缓存正文可以同时改善预览和重复搜索，但需要独立的失效与隐私设计。

## 按文档格式的进一步优化空间

2026-11-15 追加。按当前工作区 rc.11 逐格式检查；除引用已有测量外，以下均为代码审查结论，未重新跑基准。各格式的样本规模、缓存状态和解析库版本不同，收益不能互相外推。

| 格式 | 当前主要固定成本 | 可做的下一步 | 预期收益 | 风险与前置 |
|---|---|---|---|---|
| TXT／MD／LOG | `StreamReader` 解码后再复制到 8K 行缓冲；每行创建行文本、`line:N`、`TextSegment`、`SourceLocation`；空行也产出片段 | 直接 `Decoder` 管道省一次字符缓冲复制；[`Utf8.IsValid`](https://learn.microsoft.com/dotnet/api/system.text.unicode.utf8.isvalid) 做 UTF-8 字节预验证；大文件专用候选扫描 | 大日志的解码与分配中－高；逐行包装分配中 | 必须保持 BOM、GB18030、CRLF、取消和行号；跳过空行会改变提取契约 |
| CSV | 未加引号字段仍逐字符推进；每字段 `StringBuilder.ToString()`、字段 ID、`SourceLocator`；空字段也产出片段 | 未加引号字段用 `IndexOfAny`／span 一次推进；池化 8K 字符缓冲；空字段跳过需契约决策 | 大 CSV 中－高 | 跨块 CRLF、引号、错误时序和逐字符取消粒度必须保持 |
| DOCX | `MainDocumentPart.Document` 整树 DOM；每段 `StringBuilder`；`Blocks<T>` 迭代器 | `OpenXmlReader` 逐段 SAX（段落仍是小 DOM）；单 run/单文本快路径；迭代器改直接 `ChildElements` | 大文档峰值内存高；速度中 | 保持段落编号、表格编号、跳过子树和警告顺序 |
| XLSX | 共享字符串整表 DOM；每个工作表整表 DOM；每单元格 `HashSet<string>`、大写、单元格 ID、`SourceLocator` | 共享字符串和工作表都改 SAX；`(row,column)` 打包去重；大写／ID／来源延迟到有值单元格；用 `row.ChildElements` 代替每行 `Elements<Cell>()` | 大表峰值内存高；速度中－高 | 保持共享字符串预算、隐藏表编号、重复坐标错误和公式警告 |
| PPTX | 每页 `Slide` DOM；逐段 `BoundedTextBuilder`；通常文本量小 | `SlidePart` SAX 或跳过无关子树；单 run 快路径 | 小－中 | 保持顶层对象编号、表格行列和跳过规则 |
| PDF | PdfPig 解析与版面重组；每页文本字符串全部缓冲；每个空页一条警告 | 顺序枚举页面；消费后释放缓冲引用；空页警告聚合；自定义文本构建器 | 峰值内存中；速度小 | PDF 文本由 PdfPig 决定，不能为快改用 `Page.Text` 而改变空白／阅读顺序 |

### TXT／MD／LOG

- [PlainTextExtractor.cs:14](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L14) 用 `StreamReader` 解码，[PlainTextExtractor.cs:28](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L28) 再把字符复制到自己的池化缓冲；大文件存在一次额外的解码缓冲复制。[PlainTextExtractor.cs:57](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L57) 和 [PlainTextExtractor.cs:70](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L70) 每行还会创建行文本、ID、`TextSegment` 和 `SourceLocation`。
- 最直接的格式级改动是绕过 `StreamReader`，用租用的字节缓冲 + `Decoder.Convert` 直接解码到字符缓冲，按现有行状态机切分；BOM、严格回退、GB18030 和 EOF 语义保持不变。对 64 MiB 级日志应测耗时、CPU、累计分配和峰值四项，不能只看分配。
- [PlainTextExtractor.cs:154](../src/LocalDocSearch.Core/PlainTextExtractor.cs#L154) 的 `ValidateAsync` 只影响 `Utf8OrGb18030`／`Gb18030` 无 BOM 文件：UTF-8 分支可用 `Utf8.IsValid(ReadOnlySpan<byte>)` 做字节级预验证，失败才走 GB18030，避免同一文件先解码验证再解码提取。默认 `StrictUnicode` 不受影响。
- 空行跳过、专用字面量扫描属于契约或架构改动：前者会改变 `line:N` 片段集合，后者需要把词表或候选集传入提取器；目前不应当作低风险微优化。

### CSV

- [CsvTextExtractor.cs:23](../src/LocalDocSearch.Core/CsvTextExtractor.cs#L23) 仍是逐字符 `TryRead`，[CsvTextExtractor.cs:38](../src/LocalDocSearch.Core/CsvTextExtractor.cs#L38) 每遇到逗号／换行就创建一个字段片段。rc.11 已消除逐字符异步链，但普通未加引号字段仍可用 `IndexOfAny` 在缓冲内一次找到下一个特殊字符，只有引号或跨块半字段才进入逐字符状态机。
- [CsvTextExtractor.cs:79](../src/LocalDocSearch.Core/CsvTextExtractor.cs#L79) 的 `CsvCharReader` 自己分配 8 KiB 数组，而 `StreamReader` 内部还有解码缓冲；文件多时可考虑池化该数组。按块检查取消能减少逐字符调用，但会改变取消响应粒度，必须单独测。
- 空字段当前明确产出片段（[SmallFileBufferTests.cs:21](../tests/LocalDocSearch.Core.Tests/SmallFileBufferTests.cs#L21) 和 [CsvBlockParserTests.cs:24](../tests/LocalDocSearch.Core.Tests/CsvBlockParserTests.cs#L24) 有断言）。跳过空字段不会改变任何非空关键词的搜索结果，但会改变提取器输出和测试契约，需要单独批准。

### DOCX

- [WordTextExtractor.cs:21](../src/LocalDocSearch.Extractors/WordTextExtractor.cs#L21) 打开文档后，[WordTextExtractor.cs:23](../src/LocalDocSearch.Extractors/WordTextExtractor.cs#L23) 通过 `MainDocumentPart.Document.Body` 物化整份正文 DOM；大文档的主要差异在这里，而不是匹配循环。
- 可改用 `OpenXmlReader.Create(mainPart)` 顺序读取：遇到 `w:p` 时 `LoadCurrentElement()` 只物化当前段落，再复用现有 `ParagraphText`；遇到 `w:tbl`／`w:tr`／`w:tc` 维护表号、行号、列号和 `gridBefore`／`gridSpan`。跳过 `drawing`、`pict`、`txbxContent`、`del`、`AlternateContent` 时用 `reader.Skip()`，不要物化被排除子树。
- 需验证 `LoadCurrentElement()` 之后的 `Read()` 不会重复进入已物化子树，并保持“被排除段落仍参与编号”的现有规则。`Blocks<T>` 的递归迭代器可改成直接遍历 `ChildElements`，减少表格行的短期分配。
- [WordTextExtractor.cs:90](../src/LocalDocSearch.Extractors/WordTextExtractor.cs#L90) 每段都创建 `StringBuilder`；常见的“单 run、单文本、无制表符／换行”段落可以直接返回 `W.Text.Text`，其余再走 builder。更彻底的版本是用池化／精确长度的文本构建器，但需要保持预算检查和 `ToString()` 结果完全一致。

### XLSX

- 这是格式级收益最集中的一处。[ExcelTextExtractor.cs:28](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L28) 物化整个共享字符串表 DOM，[ExcelTextExtractor.cs:52](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L52) 物化整个工作表 DOM；后者在百万单元格表上通常是峰值内存和解析时间的主要来源。
- 共享字符串可用 `OpenXmlReader.Create(sharedStringPart)` 逐项读取：每到 `SharedStringItem` 只物化该项并调用现有 `RichText`，解码后的字符串列表和 32 Mi 字符预算保持不变。工作表可用 `OpenXmlReader.Create(worksheetPart)` 逐 `row`／`cell` 读取；`CellReference`、`DataType`、`CellFormula`、`CellValue`、`InlineString` 都可从元素事件或小范围 `LoadCurrentElement` 得到。
- [ExcelTextExtractor.cs:55](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L55) 每单元格用字符串 `HashSet` 去重，[ExcelTextExtractor.cs:68](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L68) 再 `ToUpperInvariant()`；`.NET 10` 对已经大写的地址会返回同一实例，但仍有扫描成本，小写地址还会分配。可改用 `(row, column)` 打包整数去重，只在真正产出片段或报错时生成大写地址。
- [ExcelTextExtractor.cs:56](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L56) 的 `data.Elements<S.Row>()` 每次遍历只分配一个迭代器，但 [ExcelTextExtractor.cs:59](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L59) 的 `row.Elements<S.Cell>()` 是每行一个；改成 `row.ChildElements` 的 struct 枚举可减少大表的短期分配。
- [ExcelTextExtractor.cs:75](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs#L75) 对每个无缓存值的公式单元格发一条带地址的警告。公式密集工作簿会重复格式化消息；可在警告详情已达上限时聚合，但会改变警告计数／消息文本，需单独决策。
- 可选：只解码被包含工作表实际引用的共享字符串。Excel 常在删除单元格后保留孤立共享字符串，跳过它们能省解码；代价是要先扫描工作表收集索引，且会改变“共享字符串累计预算”的计数口径，需先确认是否接受。

### PPTX

- [PowerPointTextExtractor.cs:20](../src/LocalDocSearch.Extractors/PowerPointTextExtractor.cs#L20) 打开演示文稿后，[PowerPointTextExtractor.cs:35](../src/LocalDocSearch.Extractors/PowerPointTextExtractor.cs#L35) 每页物化 `Slide` DOM；每页通常不大，收益低于 XLSX／DOCX。
- 若要做，按 `SlidePart` 用 `OpenXmlReader` 顺序读取，在 `p:sp`／`p:graphicFrame`／`p:grpSp` 等顶层对象处维护对象编号，遇到 `a:tbl`／`a:tr`／`a:tc` 维护行列；无关子树直接 `Skip()`。必须保留当前“顶层对象编号包括被忽略对象”的规则。
- [PowerPointTextExtractor.cs:78](../src/LocalDocSearch.Extractors/PowerPointTextExtractor.cs#L78) 每段一个 builder；单 run 段落可走快路径。该格式通常不是瓶颈，建议在 DOCX／XLSX 之后再测。

### PDF

- [PdfTextExtractor.cs:20](../src/LocalDocSearch.Extractors/PdfTextExtractor.cs#L20) 的 `PdfDocument.Open` 与 [PdfTextExtractor.cs:29](../src/LocalDocSearch.Extractors/PdfTextExtractor.cs#L29) 的 `ContentOrderTextExtractor.GetText` 是主要成本；后者负责阅读顺序和空白，改用 `Page.Text` 会改变搜索文本，不应作为纯提速。
- [PdfTextExtractor.cs:24](../src/LocalDocSearch.Extractors/PdfTextExtractor.cs#L24) 按页号 `GetPage`；可测 `GetPages()` 顺序枚举是否减少重复页树查找，同时自行计数保持 `pdf:pN` 不变。收益依赖 PdfPig 0.1.16 的页缓存实现，必须实测。
- [PdfTextExtractor.cs:33](../src/LocalDocSearch.Extractors/PdfTextExtractor.cs#L33) 每遇到无文字页面就格式化一条警告。扫描版 PDF 可聚合空页警告，但 [ReliabilityTests.cs:64](../tests/LocalDocSearch.Extractors.Tests/ReliabilityTests.cs#L64) 明确断言空页警告；聚合应保留前若干页的逐页消息和总数说明。
- PDF 的页面文本字符串在整份文档读完前一直保留在 `List<TextSegment>`；下列“消费后释放”是低风险的内存改善，不是解析加速。

### 共性改动

1. **OOXML SAX 是安全范围**：`ReadOnlyExtraction.Run` 仍然先完整解析并缓冲，SAX 只替换 DOM 的构建方式；段 ID、文字、来源、预算、警告和“晚段失败不发布”契约都可保持。需要验证 `OpenXmlReader.LoadCurrentElement()`、`Skip()` 和 `MaxCharactersInPart` 的行为。
2. **消费后释放缓冲引用**：[ReadOnlyExtraction.cs:30](../src/LocalDocSearch.Extractors/ReadOnlyExtraction.cs#L30) 当前 `foreach` 持有整个 `List<TextSegment>`；解析已经完成后，可改为按索引取出并把该槽置空，再 `yield`。这样引擎匹配期间已消费的页面／段落字符串可被回收，且不改变任何输出或异常语义。提取阶段的峰值不会因此下降。
3. **文本构建器**：[BoundedTextBuilder.cs:11](../src/LocalDocSearch.Extractors/BoundedTextBuilder.cs#L11) 每个段落／单元格／共享字符串创建一个 `StringBuilder`。池化或在单 run 时直接复用解析器字符串，可减少短期分配；收益主要是分配和 GC，必须同时报告耗时。
4. **惰性 ID／来源**：[SearchModels.cs:50](../src/LocalDocSearch.Core/SearchModels.cs#L50) 的 `TextSegment` 在当前实现中每个片段都创建 ID 和 `SourceLocator`，而引擎只在命中时读取它们。延迟生成可显著减少无命中片段的分配，但 [MatchPreviewLoader.cs:55](../src/LocalDocSearch.Desktop/MatchPreviewLoader.cs#L55) 会逐段比较 `segment.Id`，需要改成按来源组件比较，属于公开模型契约改动，暂不混入格式快路径。
5. **流式发布不是免费优化**：当前提取器在失败时保证一个片段都不产出（[Phase4ReliabilityTests.cs:111](../tests/LocalDocSearch.Extractors.Tests/Phase4ReliabilityTests.cs#L111) 的 `SmallSegmentLimit_RejectsTheWholeDocument` 断言 `published` 为空）。改成边解析边 `yield` 会改变这个提取器契约；虽然引擎仍会丢弃整文件结果，但不能把这项和 SAX、池化放在同一风险级别。
6. **按格式设置文件流**：Office 包是 ZIP 随机访问，PDF 多为顺序读取；可测试更大的 `FileStream` 缓冲或 `FileOptions.RandomAccess`／`SequentialScan`，但不要把平台相关的系统调用差异写成通用结论。

### 格式级建议顺序

1. 先做 XLSX：共享字符串 + 工作表 SAX，外加打包坐标 key 和延后大写。它的结构最规则、收益最集中，且不改变提取契约。
2. 再做 DOCX：SAX 逐段读取 + 单 run 快路径；表格坐标和“被排除段落仍编号”是最需要回归的部分。
3. 然后是低风险组合：CSV 未加引号 span 快进、`ReadOnlyExtraction` 消费后释放、PDF 顺序页枚举。
4. TXT／MD／LOG 的直接解码管道与 `Utf8.IsValid` 单独测量；它只影响大文本文件和旧编码模式。
5. PPTX 的 SAX、空行／空字段跳过、惰性 ID／来源放在最后，等契约决策或更高收益项完成后再说。

## 方案取舍

| 方案 | 目标 | 主要内容 | 收益 | 代价／风险 | 建议 |
|---|---|---|---|---|---|
| **A. 轻量固定开销 + UI** | 当前版本更快、更跟手 | UI 首屏懒加载；枚举信息分类；CSV 块解析；Excel 打包 key；自动编码字节验证；正则枚举 | 改动分散、可逐项测量；不改变首次搜索的架构上限 | 需要更新 Desktop 测试；元数据分类有诊断时序取舍；CSV／Excel 需要端到端基准 | UI 与 CSV 已在 rc.11 实施；剩余部分继续逐项做 |
| **B. 可失效的提取缓存** | 同一批文件重复搜索更快 | 按文件身份 + 大小 + mtime + 提取器版本 + 编码／范围选项缓存文字片段；用 fresh 检查或 USN／目录监听失效 | 第二次搜索可跳过 Office／PDF 解析和文本解码；对重复搜索收益最大 | 缓存容量、磁盘隐私、陈旧结果、删除／重命名、旧版本失效；仍要在缓存文字上匹配，不等于文件名索引 | 如果用户场景以反复搜索为主，优先评估 |
| **C. n-gram 候选索引** | 第一次搜索也接近即时 | 中文 2-gram、英文 2／3-gram 倒排；候选命中后回原文核验；正则保留回退 | 对大而稳定的语料，首次搜索可显著减少全文扫描 | 建索引成本、删除／重命名失效、CJK 语义、正则回退、许可和依赖 | 只有在“首次搜索也必须快”的明确需求下再做 |
| **D. 按格式解析快路径** | 大型单文件搜索更快 | XLSX／DOCX SAX；Excel 打包 key；CSV span；文本直接解码；PDF 页枚举与消费后释放 | 针对大 Office／日志文件的固定成本，和 A 的通用收益叠加 | 解析器改写需要逐格式差分与端到端基准；SAX 有 SDK 行为差异 | **A 之后的主要候选**，XLSX 先行 |

补充：

- A 不需要新增依赖，适合在当前 rc.10 基线上逐项落地。
- B 可以只缓存 Office／PDF 等高成本格式，纯文本缓存收益和内存占用要单独算；磁盘缓存含正文，需用户可见的清理／关闭方式。
- C 若使用外部索引库，应先核对许可证和依赖策略；若自建倒排，中文 2-gram 和正则回退是主要设计量。现有[搜索原理与后续计划](search-principles-and-next-steps.md#L69)也把缓存、索引列为后续独立决策。

## 建议执行顺序

- [ ] 0. 固定测量口径：5,000 文件且包含较多命中的窗口、64 MiB 日志、CSV、XLSX 各一份；拆分记录发现、提取、匹配、UI 交付四段耗时。
- [x] 1. 做 UI 首屏匹配懒加载；更新 Desktop 断言，验证展开、回收、滚动和“再显示 50 处”。（rc.11 已实施，见[结果](ui-csv-optimization-results.md)）
- [ ] 2. 用枚举信息做目录项分类，保留 fresh 打开与 EOF 检查；对照 5,000 文件窗口和 6,455 文件元数据探针。
- [x] 3a. 做 CSV 块解析，保留跨块 CRLF、引号和逐字段预算。（rc.11 已实施，见[结果](ui-csv-optimization-results.md)）
- [ ] 3b. 做 Excel 打包 key／延后大写、自动编码 `Utf8.IsValid`；两项分别独立计时。
- [ ] 4. 按格式样本先做 XLSX 共享字符串 + 工作表 SAX，再做 DOCX SAX；每项验证片段 ID、文字、来源、预算和警告顺序。
- [ ] 5. 做 CSV 未加引号 span 快进、`ReadOnlyExtraction` 消费后释放、文本直接解码管道；分别计时，不合并归因。
- [ ] 6. 如果用户主要重复搜索同一批文件，评估 B 提取缓存；如果首次搜索也必须快，再评估 C。
- [ ] 7. 任何一项完成后都保留原始正负样本，不用单次毫秒差异宣布普遍加速。

## 测量与验收

1. **结果一致性**：同一查询的匹配文件、匹配顺序、`Before/Text/After`、来源位置、片段 ID、状态和诊断计数必须与当前实现逐项一致；差异应单独解释，不能用“更快”掩盖语义变化。
2. **计时口径**：请求／查询创建是否计入要写清楚；完整性 SHA、结果摘要和来源核验放在计时外；不清 OS 缓存时不要称冷启动。
3. **资源口径**：耗时、CPU、累计分配、进程寿命工作集峰值分别报告；分配下降不等于峰值下降。
4. **完整回归**：保留六套测试的暂停、停止、预算、晚段失败、编码、预览、滚动和并发覆盖；新增 UI 懒加载、枚举分类、CSV 块解析、Excel key 的定向测试。
5. **窗口验收**：大量命中时观察结果队列背压、UI 消费速度、展开后的首屏、回收容器再进入和停止后的结果保留。
6. **缓存／索引专项**：若进入 B／C，必须新增文件修改、替换、删除、重命名、损坏缓存、索引重建、正则回退和磁盘／内存上限测试；不能把当前“无缓存、无索引”的语义直接沿用。
7. **格式专项**：SAX／快路径需要原创的大样本，例如 20 万行 × 10 列 XLSX、10 万段 DOCX、500 页 PPTX、1,000 页 PDF、64 MiB 日志和 8 MiB 宽 CSV；分别记录提取与完整引擎耗时、CPU、累计分配和峰值，并与当前实现逐段比较 ID、文字、来源、预算错误和警告顺序。不能拿坐标微基准或 200 份混合小文件结果外推。

## 暂不纳入本轮

- 继续降低 `SearchValues` 的词数门槛：4／16 词对照已出现回退，没有证据支持。
- 为提前返回而跳过完整 EOF、公式资格或末次来源检查：会改变当前“完整文件才发布”和诊断语义。
- 默认改为 4 工作者：现有证据不足以覆盖多份大型 Office／PDF、不同磁盘和内存压力。
- 把 Office／PDF 流式化直接当作提速项：解析器本身和完整发布契约仍是大头，需单独测量。
- 把进程隔离当作速度优化：它更适合可靠性／超时隔离，而不是减少解析时间。
- 根据 rc.9 正则对照的波动直接改代码：该场景没有走候选预筛，毫秒级差异包含启动、JIT 和程序集差异，需更多样本后再判断。

## 参考

- [UI 首屏与 CSV 块解析结果（rc.11）](ui-csv-optimization-results.md)
- [多关键词候选预筛验收](literal-prefilter-results.md)
- [固定开销优化 rc.10 验收](fixed-overhead-results.md)
- [1／2／4 受限并发实验](bounded-concurrency-results.md)
- [多文件效率与暂停／继续／停止](multifile-search-results.md)
- [搜索实现原理、括号能力与后续计划](search-principles-and-next-steps.md)
- [固定开销实施计划](fixed-overhead-plan.md)