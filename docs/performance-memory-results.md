# rc.7 搜索性能、内存治理与短启动名验收

日期：2026-11-13。状态：已完成。对应[实施计划](performance-memory-plan.md)。本轮针对搜索慢、OOM 风险、启动名过长和 GitHub 同步状态反馈实施；未新增 NuGet、索引、跨次全文缓存或 OCR，未执行 Git 初始化、commit、push 或公开发布。

## 1. 已交付变化

- [搜索引擎](../src/LocalDocSearch.Core/TextSearchEngine.cs)每文件复用片段匹配暂存列表，只在多项时排序，减少无命中行与密集匹配的临时分配。
- [界面队列](../src/LocalDocSearch.Desktop/ViewModels/MainWindowViewModel.cs)在有界通道满时合并一次 Background 提前消费，保留定时器兜底、64/256 容量、逐项 Add、取消与最终排序；旧轮回调通过通道身份校验退出。新增背压次数、等待时间和提前消费诊断；[文件结果](../src/LocalDocSearch.Desktop/ViewModels/FileResultViewModel.cs)按需缓存显示元数据，仍按 50 处加载并保留原 MatchItem。
- [资源模型](../src/LocalDocSearch.Core/SearchResourceLimits.cs)新增结果正文字符预算；[预览加载器](../src/LocalDocSearch.Desktop/MatchPreviewLoader.cs)使实际预览提取全进程串行，排队可取消。旧同步解析真正返回前持续持有串行门，避免快速切换叠加解析器。
- [桌面项目](../src/LocalDocSearch.Desktop/LocalDocSearch.Desktop.csproj)正式输出 `DocSearch.exe`，同步 DLL、deps/runtimeconfig、资源 URI、构建和便携说明；项目命名空间、查询、偏好和图标保持原契约。

## 2. 前后性能与内存

原版为已交付 rc.6 的原始二进制，新版为最终 rc.7 快照和同包 EXE。相同原创语料、各三轮，CLI 场景使用独立进程；源文件哈希/长度/修改时间前后核验在搜索计时外，未清 OS 缓存，首轮不称冷启动。耗时列为三轮中位数；累计分配是 GC 分配增量，工作集是进程存活期高水位，两者不是同一指标。

| 场景 | rc.6 中位耗时 | rc.7 中位耗时 | 耗时变化 | 累计分配变化 |
|---|---:|---:|---:|---:|
| 实际窗口：5,000 文件 / 10,000 命中 | 5,097 ms | 3,013 ms | 缩短 40.9% | UI 未采集此项 |
| CLI：5,000 小文件 / 10,000 命中 | 2,777.13 ms | 2,658.43 ms | 缩短 4.3% | 减少 0.03% |
| CLI：64 MiB 日志 / 78 命中，首组三轮 | 451.71 ms | 591.85 ms | 增加 31.0% | 减少 5.7% |
| CLI：200 份八格式混合文件 / 400 命中 | 369.49 ms | 390.88 ms | 增加 5.8% | 增加 1.8% |
| CLI：单文件过密命中隔离 | 77.16 ms | 52.33 ms | 缩短 32.2% | 减少 15.4% |
| CLI：整轮 200,000 命中资源上限 | 103.57 ms | 81.87 ms | 缩短 20.9% | 减少 15.1% |

实际窗口三轮为旧 `[5130, 5097, 5097]`、新 `[3045, 3013, 2991]` ms。中位工作集高水位 208,183,296 → 206,663,680 字节，下降 0.7%；最大采样私有内存的中位数 177,684,480 → 181,604,352 字节，增加 2.2%。CLI 小文件工作集高水位增加约 14.0%，其余场景也有波动。因此本轮证明了大量结果进入桌面时的提速与部分场景分配下降，没有证明峰值内存全面下降或所有格式普遍加速。

日志首组波动较大，追加 `rc.6 → rc.7 → rc.7 → rc.6` 四个独立进程，每进程三轮。各版本六次采样的中位耗时为 582.65 → 519.68 ms，缩短 10.8%；中位累计分配 299,843,948 → 280,045,452 字节，减少 6.6%。这组补充不覆盖上表原始结果，日志耗时仍受进程启动、JIT、缓存及调度影响；混合格式保留前置检查开销，不据此宣称稳定加速。

证据：[完整前后比较](../artifacts/history/performance/performance-memory-comparison-52b31f2983874f79938f1c67c5dc1fab/results.json)、[rc.6 CLI 原始数据](../artifacts/history/performance/performance-rc6-before-117b5d8f611946b6a73cafdc2b1c9b85/)、[rc.7 CLI 索引](../artifacts/history/performance/performance-rc7-after-9b796a7298ed47a1aa6a7bf2fafcc798/index.json)、[旧窗口三轮](../artifacts/history/performance/performance-ui-rc6-before-4b2d8ed9c165484b917b054ee9c6823c/results.json)、[新窗口三轮](../artifacts/history/performance/performance-ui-rc7-after-ef936e80d99248528ddd002736edd5d0/results.json)、[日志交替原始数据](../artifacts/history/performance/performance-log-balanced-84b518e43f3240b68881f29bd6847d70/results.json)及[六次采样汇总](../artifacts/history/performance/performance-log-balanced-84b518e43f3240b68881f29bd6847d70/summary.json)。所有 CLI 场景的完成状态、扫描数、匹配数、发布数及错误/警告计数前后一致。

## 3. OOM 护栏及剩余边界

| 护栏 | 当前行为 | 边界 |
|---|---|---|
| 结果正文 | 每文件 16,777,216、整轮已保留结果 33,554,432 个 UTF-16 字符；统计 Before/Text/After；单文件预算在创建子串或正则 Value 前检查，整轮预算在完整文件发布前检查 | 不包含字符串对象、列表、当前未发布文件与解析库的全部内存 |
| 文件原子发布 | 单文件字符超限整份失败；全局余量不足则停止并保留已完成文件，未合格文件不消费全局正文预算 | 不截断后冒充完整结果；现有每文件 50,000 / 整轮 200,000 命中限制保留 |
| Office ZIP | [预检](../src/LocalDocSearch.Extractors/OpenXmlPackageBudget.cs)在 SDK.Open 前读取元数据；最多 10,000 entry，单 entry 64 MiB，总声明展开量 256 MiB | ZipArchive 中央目录已先物化；伪造元数据、并发修改和 SDK DOM 仍有边界，不是恶意压缩包的硬 RAM 隔离 |
| Word / PPT / Excel | [字符构建器](../src/LocalDocSearch.Extractors/BoundedTextBuilder.cs)在 Append 前检查单段/剩余输出；Excel 共享字符串最多 32 Mi UTF-16 字符 / 200,000 项，追加及建表前校验 | SDK DOM 与 ToString 副本仍有开销；超长纯空白段也明确受限，接纳范围并非完全不变 |
| 预览 | 全进程实际提取单飞，排队取消；旧不可取消调用返回前不启动下一份预览提取 | 24k 显示上限不限制提取期；此门只协调预览，不把搜索与预览合并为全进程唯一解析器 |
| PDF / 同步解析 | 保留输入与输出预算、来源核验和正常错误反馈 | PDF 的 Open/GetPage/GetText 可能先分配再返回；未增加子进程硬内存/时间限额，也未用捕获 OOM 冒充预防 |

这些变化降低可预见的保留正文、压缩包和重复预览开销；不构成进程总体内存硬上限，也不宣称消除任意输入引起 OOM 的可能。后续若要求处理不可信超大文档时的强隔离，应另行实施可终止的解析子进程和操作系统资源限制。

## 4. 回归与独立实窗

最终独立快照构建 13 个项目，0 警告 / 0 错误。六套完整 TRX 共 **982 项通过、0 失败、0 跳过**：Core 452、Desktop 139、Extractors 134、Navigation 191、Settings 37、Benchmarks 29；较 rc.6 新增 44 项。覆盖字符预算精确/超一单位、UTF-16、整文件与全局原子性、预览互斥/排队取消/异常释放/来源变化、ZIP entry/总量/数量/取消/参数和 Append 前限制。旧预览测试仅调整等待顺序以适应新串行契约，原断言保留；NuGet 锁仅同步项目输出名，30 个实际 NuGet 依赖不变。

[最终包审计](../artifacts/history/performance/performance-memory-verified-1b37b5220c494012b54f90aefe93d7e3/final-audit.json)验证 178 份源码输入、13 份锁、解压前后 354 个文件原样、67 个文本/自有 PDB × 3 种路径形式零工作区路径泄漏、自带运行时 10.0.14。高级逻辑、正则、分组往返、125% DPI 四种结果面积、300 文档实际滚轮、四种响应预览及九帧 PE 图标均通过。结果面积为整窗 63.32% / 61.32% / 65.26% / 63.32%；300 文档 369 命中，166 次滚轮、73 次回收后推进，末 50/70 条可达且保持展开。预览 UIA 使用 Value 全文与应用内部 SelectedText 相等反馈，原 TextBox 直接选区断言另有覆盖；不把它描述为 UIA 独立读取选区。所有验收自有进程正常关闭，未操作剪贴板或外部阅读器。

## 5. 成品与 GitHub 状态

当时的浅目录入口为 `C:/LocalDocSearch/DocSearch.exe`，354 个文件曾逐哈希核对；原 rc.6 备份位于 `C:/LocalDocSearch-previous-43ca74a9983a460ab243607df1442563/`。这些浅目录后来已迁回工作区，旧备份已清理；当时未关闭用户进程，见[浅目录更新报告](../artifacts/history/maintenance/shallow-copy-rc7-43ca74a9983a460ab243607df1442563/results.json)。当前运行版本见 [rc10 优化报告](fixed-overhead-results.md)，启动时仍须整体保留所选版本的 DLL、运行时、许可证及说明。

- `LocalDocSearch-1.0.0-rc.7-win-x64.zip`（原路径：`../artifacts/releases-performance-memory-final/LocalDocSearch-1.0.0-rc.7-win-x64/LocalDocSearch-1.0.0-rc.7-win-x64.zip`；历史包已清理；当前版本见 [rc10 优化报告](fixed-overhead-results.md)），SHA256：`5f8ea1b552bc67de1bf4dabbf062c1da2da67569a6c55ae2e919015decfd37c2`。
- `DocSearch.exe` SHA256：`3a73f3225a2062b8221fe9a3c54814256c45f281f56ca16f700460ff76e05ced`。
- [包内两份启动说明修正记录](../artifacts/history/performance/performance-package-documents-809164ab80c442aca5413fc2a805af8d/results.json)：其余 351 个 manifest 文件及全部二进制原样，旧 ZIP 已保留，修正后重新生成 manifest、稳定 ZIP 和校验值并复验。
- [最终文档与成品核对](../artifacts/history/performance/performance-memory-verified-1b37b5220c494012b54f90aefe93d7e3/document-final-audit.json)。

当前工作目录实际返回 `fatal: not a git repository (or any of the parent directories): .git`；没有可识别的 remote，[贡献说明](../CONTRIBUTING.md)也记录尚未配置公开仓库。因此本轮核实的是当前副本未连接 Git 仓库，尚无远端推送时间证据；没有创建仓库或推送。系统中文 IME、原生跨屏 DPI、真正干净机器、真实杀毒与签名/公开分发仍按既有路线另行验收；原文件自动填词继续停止调试。
