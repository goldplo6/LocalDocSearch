# 第二阶段验证报告

验证日期：2026-11-09。本阶段六项工作包已实施，完成锁定依赖构建、自动回归、原创八格式样本生成与真实 Windows 窗口基本操作验证。第一阶段人工交互验收保留；本报告不等同完整 v1.0 或公开发布验收。

## 实际交付

- 八类扩展名：TXT / MD / LOG / CSV / DOCX / XLSX / PPTX / 文字型 PDF；窗口默认全部勾选。
- Core 保持无第三方依赖，新增 ITextExtractor、TextSegment 与 SourceLocator；纯文本/CSV 在 Core，Office/PDF 在独立 Extractors 模块。每个匹配保留片段 ID、UTF-16 偏移和结构来源。
- 字面量匹配、独立非重叠计数、文件级 AND/OR 和每文件一次结果保持；可跨段落、单元格、页面满足 AND，不跨不同片段拼出短语。
- CSV 记录/字段/字段起始物理行、Word 正文段落/表格、Excel 工作表/A1/值类型、PPT 幻灯片/对象/表格、PDF 实际页码可核对。Word 段落不冒充排版页码。
- 搜索范围与能力警告可见；Excel 无公式缓存、PDF 无文字页、损坏文件和超限均给出说明。打开结果仍明确为不定位。
- 提取成功才发布整文件结果；取消或晚期损坏不泄漏该文件的部分匹配，源文件保持只读。

## 环境与依赖

沿用项目本地 .NET SDK 10.0.403、Avalonia 12.1.5 与 CommunityToolkit.Mvvm 8.4.2。新增 DocumentFormat.OpenXml 3.5.1、PdfPig 0.1.16；七个项目纳入 [solution](../LocalDocSearch.slnx) 与锁定构建。Core/Extractors 测试采用 xUnit v2，Avalonia Headless 使用对应 v3 宿主。

2026-11-09 实读官方 NuGet nuspec：Open XML SDK 直接许可为 MIT，PdfPig 为 Apache-2.0；这仅核对直接包声明，完整传递依赖与分发义务仍待审查。包清单见 [依赖盘点](../artifacts/history/records/package-inventory.json)，决策见 [ADR-004](decision-log.md)。

本机装有 Office。本次解析测试没有调用 Office/COM，但尚未在无 Office 干净机复测，二者分别记录。

## 自动验证

最终执行：

```powershell
./scripts/dev.ps1 -Action Test -Locked
./scripts/dev.ps1 -Action Build -Locked
```

| 测试程序集 | 通过 | 失败 | 跳过 | 最后运行耗时 |
|---|---:|---:|---:|---|
| Core.Tests | 50 | 0 | 0 | 304 ms |
| Extractors.Tests | 51 | 0 | 0 | 939 ms |
| Desktop.Tests | 7 | 0 | 0 | 约 1 s |
| 合计 | **108** | **0** | **0** | 单元/集成耗时不是实际扫描性能基线 |

最后 Release 构建：0 警告、0 错误，增量构建 0.81 s。测试与构建均启用锁文件校验。

最终 TRX：

- [Core：08:25:34](../artifacts/history/builds/test-results/29294_ORNATETEACOASTE_2026-11-09_08_25_34_net10.0.trx)
- [Extractors：08:25:34](<../artifacts/history/builds/test-results/29294_ORNATETEACOASTE_2026-11-09_08_25_34_net10.0[1].trx>)
- [Desktop：08:25:48](../artifacts/history/builds/test-results/29294_ORNATETEACOASTE_2026-11-09_08_25_48_net10.0.trx)

覆盖的主要行为：

- 旧文本回归、严格 Unicode/BOM、中文短词、字面量符号、计数、跨行 AND、晚期解码失败和取消。
- CSV 引号/逗号/双引号、CR/LF/CRLF 多行字段、缓冲边界、UTF-16、逻辑位置、晚期畸形不发布部分结果。
- 八格式 AND 的 8 文件/16 处、八格式 `.*` 的 8 文件/8 处、隐藏表专用词零命中。每种格式提取前后 SHA-256/修改时间保持，片段 ID 稳定且唯一。
- Word 相邻 run、正文表格及嵌套表不重复；不跨段落/单元格造词；页眉页脚/批注/文本框排除。
- Excel 稀疏坐标、隐藏/VeryHidden、共享与内联富文本、phonetic 排除、原始标量、公式缓存/缺缓存提示、无有效坐标不猜测。
- PPT 演示关系顺序、对象名、表格、组合图形/备注排除；PDF 真实页号和空白页 OCR 提示。
- 四类文档损坏隔离、共享读取、已取消与途中取消、晚期损坏、解析前过滤，以及输入/part/累计输出预算。
- Desktop 八格式 AND/OR 与筛选、来源文字、1 万模拟结果的首末项虚拟化、匹配分批、高亮和 350 条取消错误积压保留。

预算测试使用低额度验证精确边界、累计与片段数独立上限、单段超限、UTF-16 代理对和非法额度，不为测试制造巨型内存分配。

## 真实 Windows 窗口验证

由项目本地 SDK 启动 Release 窗口，标题“LocalDocSearch · 八格式搜索原型”；使用 Windows UI Automation 操作本应用。最后一轮 PID 26956，检查后正常关闭，标准错误输出为空。

| 实际操作 | 窗口读取结果 |
|---|---|
| 检查默认筛选 | 八类型全部勾选 |
| mixed 目录、两个词 AND | 扫描 8、展示 8 文件、16 处匹配 |
| 切换 OR 再搜索 | 扫描 8、展示 8 文件、16 处匹配 |
| 仅保留 Excel | 扫描 1、展示 1 文件、2 处匹配 |
| 展开 Excel 结果 | Sheet2 · B15（共享字符串）/D20（内联字符串）与对应词可见 |

mixed 目录另有无扩展名说明文件，因此全格式显示跳过 1；仅 Excel 时跳过 8（七个未选格式与说明文件），这不表示这些样本解析失败。状态为“部分完成”，原因是 Office/PDF 固定搜索范围/值语义警告，自动测试确认基线没有 Error 级问题。

证据：[最终窗口控件与结果记录](../artifacts/history/records/phase-2-desktop-uia.json)、[本机复现脚本](../artifacts/history/tools/phase-2-uia-smoke.ps1)。脚本依赖本机 WindowsDesktop 6.0.11 UI Automation 程序集，仅为本机开发证据。前两次按预设完整路径寻找展开元素未命中；最终先筛选 Excel，再使用当前控件实际名称定位并验证通过。它们没有改动应用代码。

本次没有完成截图视觉审阅、真实 IME 输入、多 DPI、完整键盘或真实长列表滚动验收。PDF 其他页与 PPT 第 8 张的来源由提取器和 Headless 测试核对，未宣称在真实窗口逐项手动展开。

## 可直接试用的样本

目录：[tests/fixtures/mixed](../tests/fixtures/mixed)。使用“西瓜”“苹果”两个词；AND/OR 均为 8 文件/16 处，`.*` 为 8 文件/8 处。每格式各词各一次。

已运行原创生成器，输出八文件；DOCX/XLSX/PPTX 经 OpenXmlValidator，三页 PDF 经 PdfPig 逐页原文和词频读回。PDF 使用标准 CJK Type0 字体与显式 ToUnicode，不嵌入私有字体；目标阅读器的 CJK 渲染仍需按安装环境验证。生成器和位置约定见 [样本说明](../tests/fixtures/mixed/README)。

## 明确保留的边界

- 固定范围：Word 正文/正文表格；Excel 可见普通表的存储值/公式缓存；PPT 顶层文本框/表格；PDF 可提取文字。未覆盖区域在窗口声明，逐格式设置尚未实现。
- PDF 无 OCR，视觉布局与提取阅读顺序可能不同；Word 无排版页码；Excel 不重算或格式化显示。
- Office/PDF 输入限 128 MiB；Office 单 part 限 16 Mi 字符；四适配器累计输出限 32 Mi UTF-16 字符或 200000 片段。超限报错、不截断。此为输入/输出预算，不等于解析库峰值内存硬限制。
- 第三方同步解析的单次调用可能延迟取消；巨长文本/CSV 字段、海量匹配/警告、多个复杂 part 的峰值内存仍需阶段四压力与资源设计。
- 源文件共享修改不是一致性快照；更多真实文档、重解析点专项、无 Office 干净机及外部应用交互仍待专项验证。
- 精确定位、持久设置、OCR、索引/缓存、安装包、项目许可证与完整分发审查留在后续阶段。

后续优先按第三阶段原计划推进设置、交互与分级定位；仍保留 Exact / Section / OpenedOnly / Failed 的真实能力区分。
