# 多文件效率与暂停／继续／停止验收（rc.8）

日期：2026-11-13。状态：已完成。基线为 rc.7；最终 rc.8 已通过 1173 项回归、独立解压真实窗口及浅目录更新。主要收益是减少大量小文件的短命分配；本轮没有证明所有场景稳定提速，也没有把分配下降解释为进程峰值下降。

## 1. 时间与空间的取舍

- 当前每轮重新遍历、提取和匹配，不保存全文索引或跨次正文缓存。重复搜索需要重新支付 I/O 与 Office/PDF 解析成本，但不增加正文持久副本和缓存失效逻辑。
- 内存由完整结果、当前文件解析／匹配缓冲、64 个待交付文件结果、256 条问题、界面对象组成。虚拟化与每次 50 处的展示分页主要控制控件数量，不会删除已经保留的命中记录。
- 较大的读取缓冲适合较长文件；对几十字节的小文件，每次构造大字节／字符数组增加累计分配。rc.8 将 reader 缓冲按已打开文件的长度限定在 1–16 KiB，FileStream 使用 4 KiB，外层读取块长度受所选缓冲尺寸和片段预算约束，数组由池租用。严格解码预验证也复用池化缓冲，归还时清空。
- 长度只是缓冲大小提示，读取仍持续到 EOF；保留 BOM、严格 UTF、GB18030 预验证、CR/LF/CRLF、UTF-16 位置与文件变化核验。未通过截断文件、减少匹配或跳过来源校验换取数字。
- 保留单工作者、文件级完整发布及有界背压。增加并发可能重叠 I/O，也会增加同时存活的 Office/PDF 解析器、未提交结果和重排缓冲；本轮优先减少固定分配，没有引入无界任务或新依赖。
- 字符、ZIP 展开量等是工程预算，不是进程 RSS 硬限；第三方解析器的同步调用仍可能先分配后返回。暂停保留当前文件缓冲和句柄，继续沿用它们，暂停不是释放内存操作。

## 2. 借鉴的实现与实际落点

| 参考 | 借鉴 | 本轮边界 |
|---|---|---|
| [.NET StreamReader v10.0.0](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/StreamReader.cs) | 源码解释小缓冲对大量小文件构造成本的价值；参考默认 reader 1 KiB、FileStream 4 KiB 尺度 | 依据文件大小选择有界缓冲，并实际测量长短文件；默认值不是普遍最优值 |
| [ripgrep](https://github.com/BurntSushi/ripgrep) | 按工作负载选择读取与遍历策略，重视吞吐及内存成本 | 文本行搜索和完整 Office/PDF、文件级公式的语义不同，没有直接照搬并行度或对比绝对速度 |
| [.NET Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) | 有界 Wait 模式通过背压约束积压 | 保留原队列容量和完整结果，不丢结果换速度 |
| [.NET 协作取消](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads) | 在检查点响应控制请求 | 暂停使用异步门闩；停止用取消令牌，不挂起系统线程或忙等 |

实现入口：[自适应读取](../src/LocalDocSearch.Core/PlainTextExtractor.cs)、[暂停控制器](../src/LocalDocSearch.Core/SearchExecutionControl.cs)、[搜索检查点](../src/LocalDocSearch.Core/TextSearchEngine.cs)、[界面状态与队列消费](../src/LocalDocSearch.Desktop/ViewModels/MainWindowViewModel.cs)。

## 3. 性能结果：保留变慢样本

同一原创语料，CLI 五场景前后各三轮。many-small 与 large-log 另以旧→新→新→旧顺序运行，每组独立进程三轮、每版六样本。窗口端也进行了同样的交替复测。完整性检查在计时之外；没有清空操作系统缓存，以下不是冷缓存结果。累计分配为 GC 分配计数增量；工作集峰值为进程寿命峰值，二者不等价。

### CLI 首组（各三轮中位数）

| 场景 | rc.7 耗时 ms | rc.8 耗时 ms | 耗时变化 | 累计分配变化 |
|---|---:|---:|---|---|
| 5,000 小文件／10,000 命中 | 2887.48 | 2910.69 | 增加 0.8% | 减少 82.4% |
| 大日志 | 465.89 | 422.74 | 减少 9.3% | 减少 1.5% |
| 混合格式 | 363.58 | 393.79 | 增加 8.3% | 减少 11.4% |
| 单文件密集命中预算 | 63.97 | 53.59 | 减少 16.2% | 减少 0.2% |
| 全局结果预算 | 90.96 | 92.52 | 增加 1.7% | 减少 0.5% |

五场景的完成状态、文件／命中数量及问题计数逐项一致；资源受限场景仍保持原 Limited／Partial 语义。原始数据与峰值见[完整前后比较](../artifacts/history/performance/performance-memory-comparison-b250559b3c7748f88b42fef4d766f694/results.json)。

### CLI 交替复测（每版六样本中位数）

| 指标 | rc.7 | rc.8 | 变化 |
|---|---:|---:|---|
| 小文件耗时 | 2796.15 ms | 2731.43 ms | 减少 2.3% |
| 小文件累计分配 | 353,965,972 B | 62,409,180 B | 减少 82.4% |
| 小文件进程寿命工作集峰值 | 69,703,680 B | 73,936,896 B | 增加 6.1% |
| 大日志耗时 | 612.74 ms | 434.24 ms | 减少 29.1% |
| 大日志累计分配 | 277,844,188 B | 274,225,392 B | 减少 1.3% |
| 大日志进程寿命工作集峰值 | 64,976,896 B | 66,891,776 B | 增加 2.9% |

大日志旧版样本约 367–774 ms、新版约 344–695 ms，波动明显；不把中位数差推广成普遍收益。[交替 CLI 原始报告](../artifacts/history/performance/multifile-balanced-eec9d53125b24f279d20b89440da804b/results.json)保留所有轮次。最终版本仅另修了桌面暂停计数显示，基准程序及依赖共 24 个输出文件与已测快照逐字节相同，见[基准等价核验](../artifacts/history/performance/multifile-rc8-after-04f045fa7c4040a9bf26083d48b9a39b/final-benchmark-equivalence.json)。

### 真实窗口端到端

首组各三轮中位数为 3131→3557 ms，增加 13.6%；工作集峰值 206,266,368→200,577,024 B，采样私有内存 180,813,824→174,850,048 B。这一变慢结果保留在上面的完整比较中。

交替复测每版六轮，旧版耗时为 `[4664,4394,3225,3213,3190,3283]` ms，新版为 `[3176,3248,3298,3358,3156,3133]` ms；中位数 **3254→3212 ms，减少 1.3%**。工作集峰值中位数 205,901,824→200,206,336 B；采样私有内存中位数 180,678,656→175,292,416 B。每轮均完成 5,000 文件／10,000 命中，自有进程正常关闭，语料哈希、长度和修改时间保持一致，见[交替窗口报告](../artifacts/history/performance/multifile-ui-balanced-17e6d8ec2d6345b2a0bb8e9839574c39/results.json)。

结论：累计分配改善在两组 CLI 中一致，减少约 292 MB 的短命分配；窗口吞吐仍约为原来的水平，本轮未证明稳定的大幅提速。进一步优化应先分别测量文件打开／解码／匹配／队列／布局的占比，再独立评估受限纯文本并行；Office/PDF 并发、缓存和索引仍需各自的资源与正确性实验。

## 4. 新按钮与控制语义

- **暂停**：先显示“正在暂停”，在目录、文件、片段、发布边界等待异步门闩；已接收的完整结果排空后显示“已暂停”。界面此时使用实际展示的文件数与命中数，不沿用陈旧的节流进度。
- **继续**：沿用同一轮查询、扫描位置和已经展示的结果，不清空列表或重扫已完成文件。按钮在请求暂停及暂停状态显示“继续”。
- **停止**：运行或暂停时均可使用；取消也解除暂停等待。保留已展示的完整文件，丢弃未展示结果，明确提示本次扫描未完成；停止完成后可以重新搜索。空闲及停止处理中禁用控制按钮。
- 同步 Office/PDF 解析、单次匹配或排序存在检查粒度，不承诺立即中断；Regex 暂停等待不计入匹配预算，总搜索墙钟耗时包含暂停时间。

[真实窗口控制报告](../artifacts/history/search/search-controls-uia-3ecd50fd4c7f446aa95c4ad842452e00/results.json)的四个场景全部通过：暂停后六次观察、至少 600 ms 计数稳定，再继续到 5,000／10,000；暂停时停止保留同样的 448 文件／896 命中；运行中停止保留 818 文件，未跑完整个目录；停止后重新搜索再次完成 5,000／10,000。两次暂停确认约 900／115 ms，停止约 85／77 ms，只代表这组文本语料。运行中点击前的 UIA 观察与命令实际处理之间仍可能展示结果。

原生复现中发现暂停进度显示 192 文件，而停止后显示实际 207 文件，属于已排空队列后的显示未刷新。新增确定性回归先复现失败，再修正暂停确认时的计数；修复后 14 项搜索控制测试及四个真实窗口场景通过，保留[修复前回归](../artifacts/history/builds/multifile-validation/tests/pause-count-before-fix.trx)与[修复后回归](../artifacts/history/builds/multifile-validation/tests/pause-count-fixed.trx)。

## 5. 回归、包核验与交付

13 项目构建 0 警告、0 错误。完整回归 **1173 项全通过、0 失败、0 跳过**：Core 629、Desktop 153、Extractors 134、Navigation 191、Settings 37、Benchmarks 29。相对 rc.7 新增 191 项：154 项缓冲／编码／增长／边界，23 项核心暂停控制，14 项桌面控制。既有取消、资源、列表虚拟化和首结果取消断言保留；旧取消状态文案同步为“已停止”。

[最终包审计](../artifacts/history/performance/multifile-verified-8ea9dc573e86405aaf64857a4c843c60/final-audit.json)确认：182 个源码输入、13 份源锁、354 个包文件核验一致；67 个文件按三种路径形式扫描，工作区路径泄漏 0；包内运行时 10.0.14；九帧图标、高级逻辑／正则、300 文档实际滚轮及四个响应预览场景通过。结果区仍占整窗 61.32%–65.26%，搜索按钮没有新增一整行。控件尺寸、自动化交互和选区读回是证据；图片工具未能解码截图，未声称逐像素人工审阅，预览也不冒充原生 UIA TextPattern 选区证据。

`LocalDocSearch-1.0.0-rc.8-win-x64.zip`（原路径：`../artifacts/releases-multifile-search-final/LocalDocSearch-1.0.0-rc.8-win-x64/LocalDocSearch-1.0.0-rc.8-win-x64.zip`；历史包已清理；当前版本见 [rc10 优化报告](fixed-overhead-results.md)） SHA-256：`912fc4489dcebbd534768a1233598757fa8d7ba7b208970a3c1611f2aafcb1c9`。

当时的浅目录入口为 `C:/LocalDocSearch/DocSearch.exe`，354 个文件曾逐一匹配；rc.7 备份原位于 `C:/LocalDocSearch-previous-96aadb9392b7496a9e4912bdd888b851`。这些浅目录后来已迁回工作区，旧备份已清理；当时没有关闭用户进程，见[浅目录更新报告](../artifacts/history/maintenance/shallow-copy-rc8-96aadb9392b7496a9e4912bdd888b851/results.json)。[文档终核](../artifacts/history/performance/multifile-verified-8ea9dc573e86405aaf64857a4c843c60/document-final-audit.json)保留当时的一致性记录；当前版本见 [rc10 优化报告](fixed-overhead-results.md)。

本轮没有 commit、push 或公开发布，没有继续调试原文件自动填词。系统中文输入法、真实跨屏 DPI、干净机、真实杀毒环境及正式签名／分发仍按[环境矩阵](input-environment-results.md)单独验收。
