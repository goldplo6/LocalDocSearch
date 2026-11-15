# 高级搜索与 rc.4 验收报告

日期：2026-11-12。状态：已完成本次高级搜索交付，并直接推进了下一阶段的键盘、组合输入和缩放交互自动化；真实 Windows 输入法、多屏 DPI 与干净机环境仍待专项验收。对应[实施计划](advanced-search-plan.md)和[交互及发布前矩阵](input-environment-results.md)。

## 使用方式

主窗口原“括号分组…”已改为“高级搜索…”，窗口顶层为“逻辑搜索”和“正则匹配”两页。

- 逻辑页保留可视化分组、嵌套、取消括号、删除及同层连接，也支持切换“手写公式”，例如 `西瓜 AND (苹果 OR 荔枝)`。AND 优先于 OR，含空格、括号或运算符的文字用双引号，如 `"AND" OR "苹果 香蕉"`；没有隐式 AND。
- 手写与可视化共用经过验证的表达式树。有效公式可往返切换；无效公式即时显示原因及从 0 开始的 UTF-16 位置，禁用应用，捕获/提交接口再次验证。错误草稿不会回退执行先前有效式；取消才保留主查询。
- 正则页独立使用标准 .NET 正则，例如 `[A-Z]\d{2}\b`。应用后主窗口明确显示正则模式，再点击“开始搜索”。无效正则同样阻止应用。
- 普通主输入和可视化新增框仍按字面量处理。未添加输入会阻止应用；搜索期间禁止修改查询。关键词、手写公式、树和正则不进入偏好或聚合诊断。

## 实现与资源语义

[手写解析器](../src/LocalDocSearch.Core/LogicalQueryParser.cs)迭代解析 AND/OR、显式括号及双引号转义，保留重复关键词的位置身份。支持 `\"`、`\\`、`\n`、`\r`、`\t` 和 `\uXXXX`。源长上限 262144 个 UTF-16 单位，默认最多 64 词、每词 4096；树深最多 128、节点最多 256。最终由同一[公式校验器](../src/LocalDocSearch.Core/SearchFormula.cs)规范化，扫描前完成校验与快照。

[正则校验器](../src/LocalDocSearch.Core/RegularExpressionQuery.cs)和[搜索引擎](../src/LocalDocSearch.Core/TextSearchEngine.cs)在扫描前验证模式。默认最长 4096，单次匹配超时 100ms、单文件累计匹配预算 1000ms；累计计时排除提取与 I/O。超时是有检查粒度的预算，不是硬实时上限；取消可能等待一次匹配或第三方同步提取调用。超限文件整份丢弃，去敏报错并继续其它文件。沿用原有命中、片段、文件及遍历预算。

正则按单个提取片段执行，记录完整非空 `Match.Value`、偏移、上下文与来源，不跨独立行/段落/单元格/页面拼接。零长度结果跳过并按标准 NextMatch 推进。详情、应用内预览及原文件查找使用实际命中文字；正则模式不作为源文查找词。空白正则保留本意，不做 Trim。

## 完整构建和回归

候选在白名单源码快照中串行完成 locked restore、13 项目 Release build、六套测试、候选 RID resolve 后 locked restore、自带运行时发布与独立 ZIP 验证。构建 **0 警告、0 错误**，测试 **909/909，0 失败、0 跳过**；rc.3 的 752 项均保留。

| 测试项目 | 通过数 |
|---|---:|
| Core | 437 |
| Desktop | 102 |
| Extractors | 113 |
| Navigation | 191 |
| Settings | 37 |
| Benchmarks | 29 |
| 合计 | 909 |

本轮新增 128 项 Core 和 29 项 Desktop 用例。解析器测试包含全部 65536 个 UTF-16 code unit 的格式化/解析往返、语法位置及预算边界；正则测试覆盖实际位置、空长度、超时/取消、完整文件丢弃、预算及提取时间排除。新增 13 项高级搜索控件/集成用例和 16 项输入交互用例。测试数按 TRX 用例计数，不将循环断言另算用例。

## 独立解压后的真实窗口

[包级总报告](../artifacts/history/search/advanced-search-verified-f97ffcb1cf8b48018a85f6801bbf560a/final-audit.json)通过。测试仅操作自有进程、独立设置和原创语料，未触碰剪贴板或外部阅读器。指定不存在的共享 DOTNET_ROOT，并核实实际加载解压包内 coreclr；UIA 工具的 .NET 6 依赖仅用于探针，产品自带 .NET 10.0.14。

| 操作 | 实测结果 |
|---|---|
| 手写 `西瓜 AND (苹果 OR 荔枝)` | 8 份真值语料中 3 文件、7 处，精确为 truth-5/6/7 |
| 手写错误、取消草稿 | UTF-16 位置 13 提示，禁用应用，主查询保留 |
| 手写返回可视化后应用 | 仍为 3 文件、7 处 |
| `[A-Z]\d{2}\b` | 4 份正则语料中 orders.txt 一文件、3 处 |
| `[` | 位置 1、UnterminatedBracket，禁用应用 |
| `^|[A-Z]\d{2}\b` | 跳过零长，仍为一文件、3 处 |
| `(?<=订单 )A\d{2}` | 一文件、一处 |
| 来源预览 | 正文“订单 A12 A34”，行 1、列 4、3 字符，应用 SelectedText 读回一致 |

[高级实窗报告](../artifacts/history/search/advanced-search-uia-3055dce0735d476bad8ac0c29a9d96a6/results.json)包含操作值和截图路径。本控件没有提供原生 UIA TextPattern，因此预览证据采用 UIA ValuePattern 正文、来源坐标和应用实际 SelectedText 相等性读回；没有宣称通过 UIA 独立读取原生选区。高级集成测试另核验正则真实命中经过 MatchPreviewLoader 后的偏移/长度及 fake Find 收到实际文字；既有预览控件测试检查 TextBox.SelectedText。

[分组实窗回归](../artifacts/history/search/query-grouping-uia-f4fed36142274d068709eafa235e26a7/results.json)通过：平面 5 文件/10 处 → 分组 3/7 → 嵌套 3/7 → 取消两层 5/10 → 重复词追加 OR 后 6/11。非相邻选项报错、字面量、取消、待添加输入禁用应用和偏好排除查询均通过。

[原公式与布局报告](../artifacts/history/search/phase-5-package-uia-736342ac73cf49a5aa7005fdc7431ac7/results.json)通过；本机 DPI 120，三尺寸和最小窗口 64 词时，结果区占整窗 64.36%—73.51%。[300 文档实际滚轮](../artifacts/history/search/result-wheel-8038db9d5afe471ea9c553e246bd4632/results.json)通过：300 文件、369 处，246 次滚轮消息，末文件 50/70 处可达、触底保持展开，真实虚拟化回收后仍展开并可达末条；回收后使用 113 次、预算 160。

源码输入 158 项和 13 份源锁文件与构建记录一致；解压包 354 文件运行前后集合、长度和哈希一致；67 份文本/自有 PDB 的三种路径形式扫描为 0 泄漏。所有测试进程正常退出，源语料保持哈希、长度及修改时间。截图已保存，当前图像工具返回模型未声明图像输入，未完成视觉审阅；实际操作值和几何由 UIA 验证。

[文档终核](../artifacts/history/search/advanced-search-verified-f97ffcb1cf8b48018a85f6801bbf560a/document-final-audit.json)另检查 7 份交付文档的本地链接、源码/锁文件、包内使用说明、探针及 ZIP 哈希，并确认没有遗留自有测试进程。

## 探针修正记录

首轮探针请求不支持的 UIA TextPattern，第二轮旧分组探针尝试点击新版已禁用的应用按钮。均修正探针后在同一 SHA256 候选上重验，没有为通过探针修改产品。旧工具和失败报告保留；新分组探针明确核验 disabled、待添加文字完整保留及主查询未变。最终通过的探针哈希记录在总报告中。

## 候选与下一阶段

交付 `LocalDocSearch-1.0.0-rc.4-win-x64.zip`（原路径：`../artifacts/releases-advanced-search/LocalDocSearch-1.0.0-rc.4-win-x64/LocalDocSearch-1.0.0-rc.4-win-x64.zip`；历史包已清理；当前版本见 [rc10 优化报告](fixed-overhead-results.md)），自带运行时 10.0.14，保留 rc.3。SHA256：

```text
574befee52084a1f10a93982e06c12138c25aa64ef0533161e5ccc50d1f12cfa
```

已直接完成下一阶段可在当前工作区执行的组合态、键盘及四档缩放自动化，详见[交互/环境报告](input-environment-results.md)。当前距正式发布还剩两段收尾：①真实环境专项验收；②正式发布准备和分发验收。两段可并行准备，公开发布前均需完成；缓存、索引、OCR 不属于发布必经项。原文件自动填词继续停止调试，本次未 commit、push、签名或远程发布。
