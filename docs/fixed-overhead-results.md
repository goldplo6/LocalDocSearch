# 固定开销优化 rc.10 验收

日期：2026-11-14。状态：实现、1561 项完整回归、最终包 ABBA 与包内原生查询／预览通过。

本轮按[实施计划](fixed-overhead-plan.md)评估新方案。已先完成敏感信息模式检查并将已验收 rc.9 提交同步到 `79f9f314f0bfe658842b9bc6ea6e60066f737d5e`。SDK、会话、设置、原始测量与成品 ZIP 留在本地，不纳入源码提交。

## 实现及语义

- [ExcelTextExtractor](../src/LocalDocSearch.Extractors/ExcelTextExtractor.cs) 使用 Span 识别地址，移除 Regex Match／Group、分组子串和列名大小写临时分配。继续使用 current-culture 的 `int.TryParse(span)`，完整保留旧语法和 out 值，包括单个尾随 LF、自定义正负号、超行和超列的不同输出。外层坐标规范化、重复检查、缺失引用跳过和文档 I/O 保留。
- [TextSearchEngine](../src/LocalDocSearch.Core/TextSearchEngine.cs) 仍以原逐词 `IndexOf` 为命中枚举依据。至少 32 个去重词时配置线程安全 Lazy，遇到至少 256 字符片段才首次建立候选集，并发工作者共享。正则、少词及短片段走原路径，校验仍同步完成。
- 同一引擎的字面量／正则只在物理文本行原列号与命中列号相等时重用不可变来源记录；其他位置仍复制调整列号，段 ID、UTF-16 偏移、上下文及完整 EOF／来源检查保留。
- 暂缓缓存枚举属性替换 fresh 分类：缓存可能早于前一文件回调，处理后来被删除／替换为 reparse 的条目时会改变提取与诊断时序。不提前初始快照，不省略无命中末检查。FileInfo 的多个 getter 本来共享缓存，不按 getter 数量计算系统调用。
- UI 原本每页只创建 50 个包装对象，本轮保留现状。CSV 已 8192 字符块读，逐字符异步状态机另案测量，未计作本轮收益。

## 回归与构建

13 个项目锁定依赖构建，0 warning／error；六套实际 TRX 共 **1561 passed、0 failed、0 skipped**：Benchmarks 102、Core 834、Desktop 166、Extractors 231、Navigation 191、Settings 37。保留既有暂停／停止、虚拟化滚动、预览及资源预算断言。

新增与定向证据：

- [核心回归](../tests/LocalDocSearch.Core.Tests/FixedOverheadCoreTests.cs)：13 个案例，覆盖 1／2／4 工作者、0／255／256 字符阈值、字面量／正则、原列号 1／42 的精确位置和来源不可变。
- [Excel 差分](../tests/LocalDocSearch.Extractors.Tests/ExcelCellAddressTests.cs)：81 个案例，包含 88,222 次旧算法参考比较、Unicode／超长输入、标准及自定义文化、工作簿端到端、缺失引用无 fallback 与重复地址报错。
- [固定开销工具测试](../tests/LocalDocSearch.Benchmarks.Tests/FixedOverheadBenchmarkTests.cs)及[预筛工具回归](../tests/LocalDocSearch.Benchmarks.Tests/LiteralPrefilterBenchmarkTests.cs)：合计 52 个定向案例，新增 short-32／short-64，原 15 场景正文不变。

## 最终包测量方法

[ABBA 脚本](../scripts/measure-fixed-overhead.ps1)复制同一 Release 驱动，仅替换 Core／Extractors 两库，分别直接取已验 rc.9 与最终 rc.10 发布包。全部其他驱动文件一致；固定 rc.9 提交、源码快照、锁文件及包内两库 SHA 单独核验。发布前 benchmark 副本与最终包受 Version／CI／PathMap 等构建属性影响，不把副本 SHA 冒充实际包 SHA。

old/new/new/old 串行，每个命令新进程、重复 3 轮，共 11 类负载、44 个命令、132 个计时样本，每版本每负载 6 样本。坐标循环每轮 50 万次；5 个原搜索场景、短行 16／32／64 词、长行无命中 32／64 词。另有 40 个计时外独立搜索内容／顺序摘要，4 组各 42 个坐标输入结果与循环 totals 校验，所有版本语义一致。

坐标 delegate 绑定、输入、预热和校验置于计时外；逐次消费成功、row／column 及 checksum。未清操作系统缓存，不称冷启动；CPU、累计分配、工作集和进程寿命峰值分开报告。坐标微基准不代表整份 Excel 端到端收益。

最终包[原始汇总](../artifacts/fixed-overhead-measurement-5147210f7ae74c879872b1a5b5eb9e58/summary.json)；下表为六样本中位数，正耗时变化表示变慢。

| 负载 | rc.9 ms | rc.10 ms | 耗时变化 | 累计分配 old → new（bytes） |
| --- | ---: | ---: | ---: | ---: |
| 坐标 50 万次 | 115.3233 | 37.7442 | -67.3% | 162,381,344 → 0 |
| 多小文件 | 1757.0900 | 2437.0717 | +38.7% | 62,463,872 → 62,449,216 |
| 大日志 | 461.1972 | 471.0434 | +2.1% | 276,118,932 → 275,556,516 |
| 混合格式 | 408.9096 | 410.9294 | +0.5% | 44,988,220 → 44,919,500 |
| 密集命中 | 65.6608 | 53.1343 | -19.1% | 24,274,692 → 17,469,132 |
| 全局预算 | 112.2850 | 106.6835 | -5.0% | 101,263,524 → 73,382,216 |
| 短行 16 词 | 20.9989 | 21.1822 | +0.9% | 8,303,236 → 8,309,484 |
| 短行 32 词 | 26.4204 | 24.9853 | -5.4% | 8,336,728 → 8,316,132 |
| 短行 64 词 | 37.3526 | 37.6810 | +0.9% | 8,358,392 → 8,326,488 |
| 长行无命中 32 词 | 14.4617 | 12.2921 | -15.0% | 4,399,932 → 4,408,816 |
| 长行无命中 64 词 | 11.6751 | 12.3247 | +5.6% | 4,423,684 → 4,445,928 |

确认的是坐标循环分配移除，以及密集命中／全局预算约 28.0%／27.5% 的累计分配下降；短行 32／64 词少约 20.6／31.9 KB。没有普遍端到端加速或降峰值证据：密集命中 CPU 中位数 54.6875 → 70.3125 ms、寿命峰值 94,298,112 → 94,670,848 bytes；全局预算 CPU 均 125 ms，寿命峰值 80,103,424 → 83,314,688 bytes。CPU 有明显采样量化，保留原始值。

[初轮非最终包汇总](../artifacts/fixed-overhead-measurement-bcb149cd999d4f2b9483fd5752a6e14c/summary.json)也保留：多小文件耗时曾缩短约 13.0%，大日志曾增加约 25.7%；库身份不同，不与最终包样本混算。最终包小文件第一组 new 为 2978／2831／2820 ms，第二组 new 为 2054／1624／1727 ms，波动较大，仍完整报告 +38.7% 这一轮负面结果。[紧邻场景交替复核](../artifacts/fixed-overhead-supplement-5ba8b3ee553145b7802f555a3ab05cee/summary.json)另有 8 命令、24 计时样本、8 个计时外结果摘要；沿用最终包同一套二进制，前后逐文件 SHA 核对，原矩阵未改。多小文件中位数 3083.6619 → 3092.0638 ms（+0.27%），大日志 413.7825 → 423.4244 ms（+2.33%）。小文件 +38.7% 未在紧邻复测中重现；绝对时间和组间差异仍大，不将其简单归因为某个后台程序，也不据此宣称多小文件加速。大日志保留小幅负收益，没有把初测 +25.7% 隐去。附加报告与原始 44 组独立，独立审计只认证原始矩阵。

## 包内原生与独立核验

[原生报告](../artifacts/literal-prefilter-uia-7416dde739d1420e8192e0945ab78f09/results.json)七种查询均通过：32 词长片段无候选、稀疏／重叠／Unicode、4 词 AND／OR、8 词补充平面及重音文字、2 词 fallback。只读 inline 预览读回 `aba 西瓜`，应用反馈选中 `aba`、行 1／列 1／3 字符。UIA 无 TextPattern，证据为 Value 全文与应用内部 SelectedText 核验状态，不声称独立原生选区证明。8 个源文件的 SHA／长度／mtime 不变，包内 coreclr 实际加载，自有进程正常关闭，无全局输入或剪贴板操作。

[独立审计脚本](../scripts/verify-fixed-overhead.ps1)核对 ZIP、逐条 TRX、当前源码与快照、13 个锁、原始测量与发布库精确 SHA、旧成品未改、原生报告和四文档相对链接；结果保存于候选目录的 `final-audit.json`，不覆盖已有审计。

候选 [ZIP](../artifacts/releases-fixed-overhead/LocalDocSearch-1.0.0-rc.10-win-x64/LocalDocSearch-1.0.0-rc.10-win-x64.zip)，SHA256 `ad5e19b07963dc7e6bcf57345a18f465d4b32198f89924d413b5f22cfa38ffe2`；Windows x64 自带 .NET 10.0.14，无需单独安装运行时。旧 rc.8／rc.9 成品保留，运行目录未替换。

复测顺序：

```powershell
./scripts/release.ps1 -Version 1.0.0-rc.10 -IncludeSamples -OutputRoot ./artifacts/releases-fixed-overhead-new
./scripts/measure-fixed-overhead.ps1 -DriverDirectory CANDIDATE/build-input/tools/LocalDocSearch.Benchmarks/bin/Release/net10.0 -CandidatePackageDirectory CANDIDATE/LocalDocSearch-1.0.0-rc.10-win-x64
./scripts/verify-literal-prefilter-uia.ps1 -Executable CANDIDATE/LocalDocSearch-1.0.0-rc.10-win-x64/DocSearch.exe
./scripts/verify-fixed-overhead.ps1 -CandidateDirectory CANDIDATE -MeasurementPath MEASUREMENT/summary.json -NativeReportPath NATIVE/results.json
```

## 当前边界

无新增依赖、全文索引、跨次正文缓存或 OCR；默认单工作者不变。逻辑资源预算不等于进程硬 RSS 限制，源文件也不是一致快照。真实系统 IME、原生跨屏、干净机无 Office、杀毒软件和正式签名分发仍沿用既有待验状态。默认 Acrobat 自动填词继续停止调试。
