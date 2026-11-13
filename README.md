# LocalDocSearch

源码仓库：<https://github.com/goldplo6/LocalDocSearch>。首次上传包含当前源码、测试、规划与验收文档，以及受限并发实验脚本；SDK、编译输出、会话记录、本机设置、原始测量数据与候选 ZIP 保留在本地。历史报告中的 `artifacts/` 数据链接对应本地验收记录，GitHub 克隆中仅包含明确纳入版本控制的实验脚本；历史报告所述“未 commit／push”是当时阶段状态。

Windows 11 本地文档全文搜索项目。支持 TXT、MD、LOG、CSV、DOCX、XLSX、PPTX、文字型 PDF，提供逐项 AND/OR、可视化嵌套括号及手写逻辑公式、独立正则匹配、可选 GB18030 编码兼容、本地设置、格式范围、资源预算与聚合诊断。可以重新核验来源并在只读文字预览中选中命中，也可请求原文件查找辅助；各自按实际证据反馈结果。提取与搜索在本地执行，读取 Office 文档无需启动 Office。最新 rc.8 增加暂停／继续与停止按钮，并对小文件使用自适应读取缓冲；1173 项完整回归及独立解压实窗通过。5,000 小文件交替基准的累计分配减少约 82.4%，耗时中位数缩短约 2.3%；窗口端到端及变慢场景另行记录，不代表所有场景提速或峰值内存降低。保留 rc.7 的结果字符、Office 展开量和预览串行护栏，启动名为 `DocSearch.exe`。保留 rc.6 的 Demo 对齐布局、原创图标、响应预览和输入交互覆盖。真实环境与正式分发收尾见下列报告。默认 Acrobat 自动填词仍受首次条款弹窗阻塞，继续停止调试。

- [1／2／4 受限并发对照实验](docs/bounded-concurrency-results.md)及[实施计划](docs/bounded-concurrency-plan.md)：1247 项回归通过，混合格式引擎 4 工作者约缩短 32.0%，5000 文件实窗约缩短 36.0%；保留变慢与资源代价，默认 1，当前 rc.8 成品未换包。
- [多文件效率与暂停／继续／停止 rc.8 验收](docs/multifile-search-results.md)及[实施计划](docs/multifile-search-plan.md)
- [性能与内存治理、短启动名 rc.7 验收](docs/performance-memory-results.md)及[实施计划](docs/performance-memory-plan.md)
- [Demo 对齐与原创应用图标 rc.6 验收](docs/demo-alignment-results.md)、[实施计划](docs/demo-alignment-plan.md)及[图标设计](docs/app-icon-design.md)
- [清爽专业界面与响应式预览 rc.5 验收](docs/design-refresh-results.md)及[设计计划](docs/design-refresh-plan.md)
- [高级搜索、手写逻辑与正则的 rc.4 验收](docs/advanced-search-results.md)、[输入交互及环境矩阵](docs/input-environment-results.md)和[实施计划](docs/advanced-search-plan.md)
- [可视化括号分组与 rc.3 验收](docs/query-grouping-results.md)及[实施计划](docs/query-grouping-plan.md)
- [多文档末项滚动修复与 rc.2 验收](docs/result-scrolling-fix.md)
- [搜索实现原理、括号能力与后续计划](docs/search-principles-and-next-steps.md)
- [第五阶段计划](docs/phase-5-plan.md)
- [本地候选发布与验收](docs/release-guide.md)
- [隐私说明](docs/privacy.md)与[安全说明](docs/security.md)
- [定位选中、紧凑界面与长样本验证](docs/navigation-preview-results.md)
- [第四阶段计划](docs/phase-4-plan.md)
- [第四阶段验证与性能报告](docs/phase-4-results.md)
- [第三阶段计划](docs/phase-3-plan.md)
- [第三阶段验证报告](docs/phase-3-results.md)
- [第二阶段计划](docs/phase-2-plan.md)
- [第二阶段验证报告](docs/phase-2-results.md)
- [技术决策](docs/decision-log.md)
- [第一阶段报告与人工待测项](docs/spike-results.md)
- [原始需求规格](LocalDocSearch-规划文档合集/02-软件需求规格说明书.md)

## 启动与开发

在项目根目录使用 PowerShell 7。固定工具链为 .NET SDK 10.0.403，Avalonia 12.1.5、CommunityToolkit.Mvvm 8.4.2；文档适配器使用 DocumentFormat.OpenXml 3.5.1 与 PdfPig 0.1.16。版本由 [SDK 配置](global.json)、[中央依赖配置](Directory.Packages.props) 和各项目锁文件管理。

本机已安装项目本地 SDK，直接运行：

```powershell
./scripts/dev.ps1 -Action Run -Locked
```

新机器先准备 SDK，再构建、测试和启动：

```powershell
./scripts/bootstrap-sdk.ps1
./scripts/dev.ps1 -Action Build -Locked
./scripts/dev.ps1 -Action Test -Locked
./scripts/dev.ps1 -Action Run -Locked
```

[SDK 引导脚本](scripts/bootstrap-sdk.ps1) 下载微软官方 SDK 并核对 SHA-512，放入项目的 `.tools/dotnet`，不修改系统 PATH。[开发脚本](scripts/dev.ps1) 优先使用该 SDK，并关闭 .NET CLI 遥测。首次 SDK 下载及 NuGet 还原需要联网；应用搜索无网络服务依赖。离线构建另需预先准备 SDK 和包缓存。

`-Locked` 校验依赖锁文件；更新依赖时先修改中央版本，再运行不带此参数的 Restore，检查锁文件并重新测试。`dev.ps1 -Action Publish` 仍生成框架依赖的开发输出。Windows x64 自带运行时的本地候选改用独立发布流程：

```powershell
./scripts/release.ps1 -Version 1.0.0-rc.1 -IncludeSamples
```

它在白名单源码快照中构建和运行全部测试，生成 ZIP、文件清单、SHA-256 与许可证据；保留源锁文件，不覆盖同名候选目录。详见[发布指南](docs/release-guide.md)。

## 八格式试用

1. 选择 [mixed 原创样本目录](tests/fixtures/mixed)，八种格式默认全选。
2. 添加“西瓜”，选择下一连接 AND 或 OR，再添加“苹果”并点击“开始搜索”。两种连接都应返回 **8 个文件、16 处匹配**，每个文件两个词各一次；已添加词之间的连接可点击修改，以顶部公式为准。
3. 展开结果核对来源：Excel 的 Sheet2!B15 / D20、PPT 的第 1 / 8 张、PDF 的第 1 / 3 页，以及 Word 段落/表格、CSV 逻辑记录/列。
4. 单独搜索 `.*`，应返回 **8 个文件、8 处匹配**，它按字面量处理。默认范围下，隐藏表专用词“隐藏专用词”为零命中；开启 Excel 隐藏表选项后可匹配。详见 [样本说明](tests/fixtures/mixed/README)。
5. 每文件只出现一次；详情每次增加 50 处，总计保留完整已提取匹配。“暂停”先等待当前检查点并展示已接收结果，显示“已暂停”后点击“继续”沿用本轮查询；“停止”保留已展示结果与已收到的问题，丢弃待展示文件，结束后可重新搜索。同步解析操作需返回检查点后响应，暂停保留当前文件状态与缓冲。

原来的 [text 样本](tests/fixtures/text) 仍保留：两词 AND 为 2 文件/6 处，OR 为 3 文件/7 处。

样本由 [原创夹具生成器](tools/LocalDocSearch.Fixtures/SampleDocuments.cs) 重建；先运行 Build，再执行：

```powershell
./.tools/dotnet/dotnet.exe run --project tools/LocalDocSearch.Fixtures/LocalDocSearch.Fixtures.csproj -c Release --no-build --no-restore -- tests/fixtures/mixed
```

生成器只重写该目录中的八个固定样本。应用搜索只读源文件；生成夹具是独立的开发操作。

## 超长文档试用

直接打开 [200 页 PDF](tests/fixtures/long-documents/原创长文档-200页.pdf)、[200 内容页 Word](tests/fixtures/long-documents/原创长文档-200页.docx)、[100,000 行 TXT](tests/fixtures/long-documents/原创长文本-100000行.txt) 或 [20,000 条记录 CSV](tests/fixtures/long-documents/原创长表格-20000记录.csv)。这些样本合计约 4.87 MiB，主要验证晚位置与重复词，不代表大字节性能负载；64/128/256 MiB 日志性能另见第四阶段报告。Word 含 199 个手动分页符，具体打印页数可能受字体与环境影响。

选择该目录搜索“长文定位西瓜”：**4 文件、8 处**；PDF 位于第 150 / 200 页，Word 位于正文段落 1798 / 2398，TXT 位于 75,000 / 100,000 行，CSV 位于含表头记录 15,001 / 20,001 的第 3 字段。搜索“长文末尾定位”：**4 文件、4 处**。点击结果上的“预览”（定位并选中）或双击匹配，在只读预览中核验所选文字。详见 [完整说明与重建命令](tests/fixtures/long-documents/README)。

## 设置、编码与定位

点击窗口右上角“搜索设置…”：编码、每侧上下文 0–500 字符、四种排序、Word 正文/表格、Excel 隐藏及深度隐藏工作表、PPT 文本框/表格均可选择。保存后用于下一次搜索；取消保留原设置。

编码仅影响 TXT/MD/LOG/CSV，BOM 始终优先：

| 模式 | 无 BOM 文件 |
|---|---|
| 严格 Unicode（默认） | 严格 UTF-8；解码错误明确报告 |
| UTF-8 优先，失败后 GB18030 | 完整校验 UTF-8；失败后严格尝试 GB18030，使用回退时提示 |
| 无 BOM 强制 GB18030 | 明确知道文件编码时使用；不猜测损坏文字 |

原混合样本 CSV 是合法 UTF-8。外部程序显示乱码不代表源字节损坏；按正确编码解码、正确另存 UTF-8 后仍能匹配。已保存成问号或替代字符的内容不会被猜测还原。

选择 [编码试用目录](tests/fixtures/encoding)，添加“西瓜”和“苹果”：默认模式 **3 文件、6 处**；选择“UTF-8 优先，失败后 GB18030”后 **4 文件、8 处**。正确另存的 UTF-8 文件在两种模式下均能匹配。详见 [编码说明](tests/fixtures/encoding/README)。

偏好保存在 `%LOCALAPPDATA%/LocalDocSearch/settings.json`：根目录、类型过滤、AND/OR、大小写、编码、上下文、排序、范围及 Office 选项；不保存关键词、正文或搜索结果。损坏配置使用默认值并提示，原文件保留至用户显式保存。开发验证可通过桌面程序参数 `--settings <配置路径>` 隔离配置。

点击“预览”（定位并选中）或双击结果/匹配，在宽度至少 1100 DIP 的主窗口右侧打开只读文字预览，窄窗口使用独立预览窗；侧栏可关闭或弹出，缩窄窗口会转为独立预览，关闭后恢复结果宽度，不需要 Office。打开原文件、原文件查找和目录定位等低频动作位于文件/匹配行的更多操作菜单。它重新使用搜索时的编码与范围核对来源片段、结构位置、UTF-16 偏移和完整命中上下文；只有文件完整读取成功、修改时间与前后元数据一致时才显示并选中本次命中，不会跳到同词的第一处。最多展示命中附近 24,000 个字符。PDF 显示真实来源页码和该页的提取文字，预览不还原原 PDF 排版；文字确实由只读 TextBox 的选区读回核验。文件变化或来源失配时明确报告定位未完成。

展开结果后的“原程序定位首条”调用 Windows/Office，Office 自动化默认关闭，需在设置中开启。结果分级为：

- `Exact`：Excel 完整路径、工作表及实际单格选区全部读回一致；或 Word 正文、PPT 顶层文本框的完整路径、结构来源、唯一上下文及实际文字选区通过独立核验。
- `Section`：PowerPoint 完整文稿路径与幻灯片序号读回一致；文字选区未核验时保持此级，PPT 表格也保持此级。
- `OpenedOnly`：仅请求打开，或位置未通过核验。任意默认 PDF 阅读器和文本编辑器仍属此级；Word 表格、修订文档或含字段目标段落会降级。
- `Failed`：打开或核验失败；Office 超时会停止等待，正在执行的调用可能稍后完成，不重复打开、不结束 Office。

“原文件查找”先由系统关联程序打开文件，再尝试识别目标文档和可编辑查找框。`SearchFieldFilled` 只代表查找框填入的实际命中词已经读回核验，不表示选中了搜索结果里的特定 occurrence，也不升级为 `Exact`；`ManualFindRequired` 会保留手动查找词，可从圆形“!”中主动复制；打开或执行失败报告 `Failed`。辅助流程不自动读取或覆盖剪贴板，不向未核验的窗口盲发快捷键。“所在目录”使用 Windows Shell 的 PIDL 接口打开文件所在目录并选择文件；360 是否提示仍需在用户环境实测。

独立的“原程序定位首条”仍按上面的四级标准判断。Office 自动化新开文档使用只读保护视图；已打开的同路径文件保留原有状态，不保存、不关闭。保护视图限制导航时按实际能力降级。Office 等待上限 12 秒，全应用同时只保留一个未完成自动化调用。

## 搜索与格式约定

- 普通输入按字面量连续子串搜索，中文一字、二字均可匹配，默认不区分大小写。“下一连接”决定新词前的 AND/OR；普通查询已有连接可点击切换。顶部“高级搜索…”的逻辑页支持可视化分组、嵌套、取消括号、删除及连接修改，也可切到手写公式直接输入 `西瓜 AND (苹果 OR 荔枝)`。AND 优先于 OR，显式括号改变顺序；相邻文字须显式连接，含空格、括号或运算符的文字用双引号，如 `"AND" OR "苹果 香蕉"`。错误显示 UTF-16 位置并阻止应用；修正后可返回可视化，显示与执行共用表达式树。应用后再点“开始搜索”，取消保留原查询。主窗口和可视化新增框中的 AND/OR、括号仍按字面量处理。重复词保留独立位置，相同文字只提取、计数一次。公式在文件级判断，可跨段落/单元格/页面；独立片段不拼接出短语。
- 高级搜索的正则页使用标准 .NET 正则，例如 `[A-Z]\d{2}\b`；与逻辑词表分开，按单个提取片段匹配整体命中，保留真实文字、偏移和来源。零长度匹配跳过；语法错误阻止应用。默认最长 4096 个 UTF-16 单位、单次匹配预算 100ms、单文件累计匹配预算 1000ms，超时文件丢弃完整结果后继续其它文件；检查有粒度。详情、只读预览和原文件查找使用真实命中文字。查询内容、表达式树和正则不保存到偏好。
- TXT/MD/LOG：默认严格 UTF-8 或 BOM 指明的 UTF-8/16/32；可选 GB18030 兼容。按物理行，行列从 1 开始，列采用 UTF-16 单元。
- CSV：逗号分隔，含表头、引号转义和跨行字段；显示逻辑记录、字段编号与字段起始物理行。CRLF/CR 在字段内规范化为 LF，不自动猜分隔符。
- Word：正文段落和正文表格；合并同段连续 run，不跨段落/单元格拼词。页眉页脚、批注、文本框及修订删除文字不搜索。结构段落号不是排版页码。
- Excel：默认可见普通工作表，可选隐藏/深度隐藏表；提取共享/内联字符串、原始数字/布尔/错误/日期值及公式缓存。来源标注值类型；不重算公式、不还原格式化显示、不搜索公式源码。无公式缓存会警告。
- PPT：按演示关系中的幻灯片顺序提取顶层文本框和表格段落；不含备注、组合图形、母版、图表或 SmartArt。
- PDF：逐页提取文字，保留真实页码；阅读顺序可能与视觉不同，无文字页面明确提示未启用 OCR。

窗口提供逐格式范围开关及持久设置；关闭的区域不搜索，未支持区域明确禁用。主要空间留给搜索结果与命中片段；格式/大小写收在弹出菜单，说明、预算、能力提示与诊断收在圆形“!”入口。格式能力提示也能从文件的“!”查看，搜索状态可为“部分完成”；这不等于文档损坏，请区分提示与错误。

解析前按扩展名过滤，跳过临时文件及遍历遇到的重解析点；根目录及祖先含重解析点时给出参数错误。文件完整提取成功后才发布该文件结果，损坏、解码错误、取消或超限不发布部分文件。Office/PDF 限制单文件 128 MiB、Office 单 part 16 Mi 字符、累计输出 32 Mi UTF-16 字符/200000 片段；超限明确报错，不静默截断。rc.7 在 Open XML SDK 打开前预检 ZIP 元数据：最多 10,000 个 entry、单 entry 64 MiB、总声明展开量 256 MiB，并在 Word/PPT 字符追加及 Excel 共享字符串建表前检查预算。元数据、SDK DOM 和 PDF 单次解析仍有额外内存边界，详见[本轮报告](docs/performance-memory-results.md)。

## 资源预算与诊断

默认限制单段/物理行/CSV 字段为 1,048,576 个 UTF-16 字符、每文件 50,000 处匹配、整轮 200,000 处或 20,000 个命中文件、100,000 个候选文件；遍历深度 128、待扫描目录 20,000。查询最多 64 个关键词、每词 4,096 字符。rc.7 另限制每文件结果正文为 16,777,216、整轮已保留结果正文为 33,554,432 个 UTF-16 字符，统计每处命中的 Before/Text/After；单文件预算在子串创建前检查，整轮预算在完整文件发布前检查。这些字符数不包含对象、当前未发布文件和第三方解析器的全部开销。界面暂不提供预算编辑，开发者可注入 [SearchResourceLimits](src/LocalDocSearch.Core/SearchResourceLimits.cs)。

- 单文件超限整份失败，继续其它文件；不把截断的文件当作完整结果。
- 全局出现超预算的额外项时标为“达到资源上限”，保留完整文件并说明扫描尚未完成。可以缩小目录或提高关键词精度后重搜。
- 全局保留至多 1,000 条诊断详情和一条省略聚合说明；每文件保留至多 100 条警告详情和一条聚合说明。错误、警告与省略总数仍完整计数。
- 结果和诊断使用有界通道，最终排序批量更新；取消后停止新增结果，并排空已接收的诊断。

“复制诊断摘要”仅由用户主动触发，包含聚合计数、预算、耗时、运行时及基础系统信息；排除根目录、文件路径、关键词、正文和异常原文。不自动写诊断日志或发送遥测。

[第四阶段报告](docs/phase-4-results.md)记录 5,000 文件、64/128/256 MiB 日志、200 份八格式文件、200,000 处结果与错误洪流实测，以及前后版本对照。基准只计数，实际窗口保留结果，两者内存与耗时分别记录。该历史阶段未显示整体加速，先完成资源治理和可靠基线。[rc.7 性能与内存报告](docs/performance-memory-results.md)另以相同原创语料比较已交付 rc.6 与新版：真实窗口耗时缩短约 40.9%，密集场景累计分配减少约 15%；日志和混合格式未证明普遍加速，峰值工作集也未全面下降。

## 结构与验证

| 部分 | 职责 |
|---|---|
| [Core](src/LocalDocSearch.Core/LocalDocSearch.Core.csproj) | 无第三方依赖的搜索模型、统一片段契约、扫描、纯文本/CSV 提取 |
| [Extractors](src/LocalDocSearch.Extractors/LocalDocSearch.Extractors.csproj) | Office/PDF 适配、来源与能力说明、提取预算 |
| [Settings](src/LocalDocSearch.Settings/LocalDocSearch.Settings.csproj) | 偏好校验、损坏配置反馈、原子保存 |
| [Navigation](src/LocalDocSearch.Navigation/LocalDocSearch.Navigation.csproj) | Windows 打开、STA Office 定位与证据分级 |
| [Desktop](src/LocalDocSearch.Desktop/LocalDocSearch.Desktop.csproj) | Avalonia 窗口、设置、筛选、上下文、排序和匹配动作 |
| [Fixtures](tools/LocalDocSearch.Fixtures/LocalDocSearch.Fixtures.csproj) | 八格式原创样本生成与 Open XML/PDF 读回校验 |
| [Benchmarks](tools/LocalDocSearch.Benchmarks/README.md) | 有所有权与完整性校验的原创规模语料、串行基准和聚合报告 |
| [Core.Tests](tests/LocalDocSearch.Core.Tests/SegmentAndCsvTests.cs) | 原搜索回归、CSV/片段边界、编码、取消与只读性 |
| [Extractors.Tests](tests/LocalDocSearch.Extractors.Tests/SampleIntegrationTests.cs) | 全格式计数/来源、哈希、格式边界、错误隔离与预算 |
| [Desktop.Tests](tests/LocalDocSearch.Desktop.Tests/MixedFormatsTests.cs) | 八格式 ViewModel、来源、筛选及原有万条列表/高亮/取消回归 |

另有 [设置回归](tests/LocalDocSearch.Settings.Tests/JsonSettingsStoreTests.cs)、[导航回归](tests/LocalDocSearch.Navigation.Tests/WindowsFileNavigatorTests.cs) 和 [基准工具回归](tests/LocalDocSearch.Benchmarks.Tests/BenchmarkTests.cs)。最新 rc.8 完整回归 **1173 项全通过、0 失败、0 跳过**，其中核心 629 项、桌面 153 项、提取器 134 项、导航 191 项、设置 37 项、基准 29 项；相对 rc.7 新增 191 项缓冲编码／边界、暂停门闩及界面控制用例，详见[本轮验收报告](docs/multifile-search-results.md)。rc.7 的 982 项及性能／内存护栏证据见[前轮报告](docs/performance-memory-results.md)。rc.6 的 938 项及 11 项 Demo 对齐、品牌资源、格式徽标和高级页用例见[对齐报告](docs/demo-alignment-results.md)。rc.5 的 927 项及 18 项桌面预览生命周期、响应布局和高级界面用例见[界面改版报告](docs/design-refresh-results.md)。rc.4 的 909 项及手写逻辑、正则和输入交互证据见[高级搜索报告](docs/advanced-search-results.md)和[交互/环境覆盖](docs/input-environment-results.md)。rc.3 的 752 项与分组证据保留在[括号分组报告](docs/query-grouping-results.md)，rc.2 的 689 项及末项滚动证据保留在[滚动修复报告](docs/result-scrolling-fix.md)。此前 rc.1 的 683 项及公式、原文件动作和候选验收见[第五阶段报告](docs/phase-5-results.md)。历史选区、窗口面积及长样本证据见[定位与界面验证](docs/navigation-preview-results.md)；原性能基线与资源治理证据见[第四阶段报告](docs/phase-4-results.md)。候选流程将六项目 TRX 分别写入候选目录的 `test-results`；开发产物和本地工具链由[忽略配置](.gitignore)排除。应用仅持久保存前述偏好，包括搜索根目录；不保存正文、关键词、公式、正则或结果，不添加遥测。开发测试产物包含原创夹具路径。

## 当前边界

本机 125% DPI 的真实窗口已验证 5,000 文件、末项 End 导航、资源上限、错误聚合、取消与重新搜索；rc.2 已通过 100/300 文档末项展开、实际滚轮到末条、分批加载及滚离回收恢复；rc.8 独立解压复验了 300 文档滚轮、分组与手写往返、语法错误阻止、正则真实命中及响应预览四场景，新增暂停后计数稳定、继续完成、暂停中停止、运行中停止及下一轮重置验证，并确认 EXE 的九帧图标与源 ICO 一致；未开预览时，结果区占整窗 61.32%–65.26%。组合态、Enter/Esc/Tab 及 100%/125%/150%/200% 合成缩放已通过；系统 Windows 中文输入法、原生跨屏 DPI 与干净机仍按[环境矩阵](docs/input-environment-results.md)待测，距正式发布还剩真实环境专项验收与分发收尾两段。外层 UIA ScrollPattern 仍待完善。一万文件模拟回归检查虚拟化、实际指针滚动与加载按钮，不代表扫描性能。行/字段、匹配、遍历与诊断预算已启用，但不等于第三方解析器或进程整体内存硬限；第三方同步解析的单次调用可能延迟取消。共享修改中的文件不是一致性快照。

已验证 Word 正文和 PPT 顶层文字的原程序选中，以及无需 Office 的八格式只读文字预览。原文件查找采用目标与控件核验；本机 Acrobat 被首次使用条款弹窗阻塞，自动填词尚未实机通过；本轮已停止继续调试，详情见第五阶段报告。项目采用 [MIT](LICENSE)，30 个实际 NuGet 包及自带运行时分别保留版本、包哈希与许可证/NOTICE 证据。任意默认 PDF 阅读器跳页、Word 表格等复杂结构、保护视图版本矩阵、OCR、索引与缓存、安装程序仍为后续工作；本地候选不等同公开发布、签名、干净机或 360 验收完成。
