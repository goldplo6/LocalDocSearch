# LocalDocSearch 原创语料基准

独立 CLI，仅对它新生成且通过 marker、manifest、逐文件 SHA-256、长度和 LastWriteTimeUtc 核对的语料执行搜索。生产 CLI 没有删除、覆盖、修复或清理命令。未登记文件、链接路径、缺失或变化的文件均使运行失败。只使用现有 BCL、Core、Extractors 和 Fixtures ProjectReference；没有新增包。

## 精确命令

由集成方先统一 restore/build。以下从仓库根执行，不启动 GUI 或 Office：

```powershell
# 新根应不存在或完全为空；--quick 仍生成至少 64 MiB 短行日志。
dotnet run --project tools/LocalDocSearch.Benchmarks/LocalDocSearch.Benchmarks.csproj -c Release --no-build -- prepare C:\Temp\lds-bench-quick --quick

# standard: 5000 小文件；可选 128 或 256 MiB，最大 256。
dotnet run --project tools/LocalDocSearch.Benchmarks/LocalDocSearch.Benchmarks.csproj -c Release --no-build -- prepare C:\Temp\lds-bench-standard --log-mib 128

# 每条 run 是独立进程；repeat 是同一进程中的总搜索轮数，默认 3，范围 1..20。
dotnet run --project tools/LocalDocSearch.Benchmarks/LocalDocSearch.Benchmarks.csproj -c Release --no-build -- run C:\Temp\lds-bench-quick many-small --repeat 3
dotnet run --project tools/LocalDocSearch.Benchmarks/LocalDocSearch.Benchmarks.csproj -c Release --no-build -- run C:\Temp\lds-bench-quick mixed --repeat 3 --output C:\Temp\lds-mixed-new.json

# 逐场景新进程；检查每个退出码。所有输出均为 JSON。
$scenarios = 'many-small','large-log','mixed','long-line','long-field','dense-file','global-budget','errors','literal-and','cancel'
foreach ($scenario in $scenarios) {
    dotnet tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/LocalDocSearch.Benchmarks.dll run C:\Temp\lds-bench-quick $scenario --repeat 3
    if ($LASTEXITCODE -ne 0) { throw "benchmark scenario failed: $scenario" }
}

# 父统一 restore 后的局部测试。避免与其它代理的 Core/Extractors 构建同时执行。
dotnet test tests/LocalDocSearch.Benchmarks.Tests/LocalDocSearch.Benchmarks.Tests.csproj -c Release --no-restore
```

`--output` 是显式指定的新文件，父目录须已经存在，并且文件必须位于语料根之外。没有默认磁盘日志，已有输出文件也不覆盖。返回 0 表示所有预期通过；2 表示本次场景的状态、计数、整文件发布或取消语义不一致，JSON 每轮 `mismatches` 给出固定错误码；1 表示参数、所有权、完整性或执行错误。错误消息也只输出固定分类码，不输出异常原文或路径。

## 原创场景及稳定预期

| 场景 | 输入及默认限额 | 搜索预期 |
| --- | --- | --- |
| many-small | standard 5000 / quick 200 原创两行 TXT | 同一文件跨行 AND，分别 5000/200 个文件、10000/400 处；Completed |
| large-log | 64..256 MiB 短行日志，每 8192 行出现一次测试词 | 1 文件；manifest 记录实际 bytes、行数及匹配数；Completed |
| mixed | 工具调用现有原创 Fixtures 生成一套八格式，再复制到 25 个编号目录 | 200 文件、400 处、默认能力 warning 100，Partial |
| long-line | 1048577 UTF-16 字符的超长物理行；此前已有可匹配行 | 超长文件整体失败；幸存文件 2 处；1 error；Partial |
| long-field | 1048577 UTF-16 字符 CSV 字段；同文件另有匹配字段 | 超长文件整体失败；幸存文件 2 处；1 error；Partial |
| dense-file | 一个文件 50001 处；默认单文件上限 50000 | 密集文件整体失败；幸存文件 2 处；1 error；Partial |
| global-budget | 41 个文件各 5000 处；默认总上限 200000 | Limited、40 个完整文件、200000 处、1 条全局资源限额 error；每个发布文件恰好 5000 处 |
| errors | standard 1200 / quick 1105 个无 BOM 非法 UTF-8 文件，另有幸存者 | 总 ErrorCount 精确，issue 回调明细不超过 1001，SuppressedIssueCount 大于零；幸存者 2 处；Partial |
| literal-and | 中文词与 `.*` 分属同文件两行，另含单词文件与正则诱饵 | 仅 both 文件 2 处；证明逐文件 AND 以及字面量语义；Completed |
| cancel | 复用 many-small 原创语料；在第一个同步 FileResult 回调内请求取消 | Cancelled；仅发布 1 个完整文件、2 处；请求后新增结果 0；任务结束 |

64 MiB 配置通常约 70 MiB 原创数据，另有 manifest；实际总 bytes 在 prepare 输出中。quick 为 1558 文件，standard 为 6453 文件，均不含 marker/manifest 两个控制文件。日志 bytes 至少达到目标，最后一个短行可略微超出目标。生成上限为日志 256 MiB、manifest 文件数 10000、语料总量 512 MiB；CLI 不接受任意文件数或任意模板路径。standard 和 quick 的异常预算场景保持相同强度（errors 文件数略有不同），quick 仅缩减 many-small。

## 指标语义和边界

- `elapsedMilliseconds` 仅含 SearchAsync 调用到任务结束的墙钟耗时；`firstResultMilliseconds` 从同一起点到第一个完整文件同步回调。没有结果时字段省略。首轮标记 `first-round`，其后标记 `repeat`，提供首轮、重复及首结果的 min/median/max。
- **未清空 OS 文件缓存。** 前置完整性 hash 会读取所有文件，本身可能使缓存变热。因此首轮绝不称冷缓存，也不把重复轮次差异归因于磁盘冷暖。不同场景请用独立进程。
- `processPeakWorkingSetBytesLifetime` 是操作系统报告的整个 CLI 进程寿命累计高水位，包含前置验证和所有此前轮次，绝不是某轮的增量，也不是精确的每次搜索峰值；另有轮末 WorkingSet。工具不 spawn 子进程。
- 托管堆 before/after 使用 `GC.GetTotalMemory(false)`，不强制 GC。`managedHeapSizeBytesAtLastGc` / fragmented 来自最近一次 GC 的 `GetGCMemoryInfo()`，可能早于当前搜索。总分配为进程级 `GC.GetTotalAllocatedBytes(precise:true)` 差值，包含搜索、回调和其间其他进程内活动；不是存活内存或单线程分配。GC0/1/2 是对应进程累计集合次数差值，包含高代集合的低代计数语义。
- 搜索前后逐文件 SHA-256、字节长度、LastWriteTimeUtc，以及控制文件 hash/mtime 均核对。前后校验耗时独立报告并排除在搜索计时和该轮分配差值之外；但会影响进程寿命峰值与 OS 缓存。prepare 也在写完 manifest 后完整读回核对；搜索不会写语料。
- 取消的 requestedAt 与 requestToCompletion 均用真实 Stopwatch 时间；无固定毫秒或 RAM 阈值。触发点是文件结果回调，是**文件边界取消**，没有声称测得同步第三方解析调用内部的抢占延迟。若某文件解析耗时长，会完整计入首结果延迟。任何请求后结果回调都使该轮失败。
- 报告含运行时、OS、架构、逻辑处理器数、GC 总可用内存及 Stopwatch 频率，不含机器名、用户名。输出只有工具原创场景标识、预期和聚合统计；不保留 FileResult、正文、关键词、查询、文件名、文件路径、issue 消息或 Core 的自由文本 LimitReason。`omittedFields` 明确声明省略字段。manifest 只为本工具的原创文件保留相对文件名/hash，不接收用户文件。
- 测试断言稳定语义、计数、只读完整性、路径防护与 JSON 隐私；不以耗时或机器内存大小判定性能通过。机器型号、存储、杀毒扫描、后台负载和运行配置都会影响结果，跨机器比较前应另行记录一致的外部条件。

## 受限文件并发实验（1 / 2 / 4）

`run` 新增 `--workers 1|2|4`，默认 1；`BenchmarkRunner.RunAsync(root, scenario, repeat = 3, workers = 1)` 保持旧调用兼容。其它整数在读取语料前返回 `invalid-workers`。schemaVersion 继续为 1，原有计数/状态断言保持不变；只追加字段。

```powershell
# 使用父方统一构建的同一 binary，不在这里触发 restore/build。
dotnet tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/LocalDocSearch.Benchmarks.dll run artifacts/phase-4-corpus-standard-64 many-small --workers 4 --repeat 3

dotnet tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/LocalDocSearch.Benchmarks.dll control artifacts/phase-4-corpus-standard-64 mixed --workers 4

# 所有测量进程严格串行；三个参数均可显式传入。
pwsh -File artifacts/measure-concurrency-experiment.ps1 -BenchmarkDll tools/LocalDocSearch.Benchmarks/bin/Release/net10.0/LocalDocSearch.Benchmarks.dll -DotnetPath dotnet -CorpusRoot artifacts/phase-4-corpus-standard-64
```

- 脚本复用 standard-64 原创语料（6453 个登记文件），不生成、修改或清理它。many-small、large-log、mixed、dense-file、global-budget 各按 **1,2,4,4,2,1** 顺序启动六个新进程，每进程 repeat 3，因此每个 workers 有 6 个性能样本。errors / literal-and / cancel 每个 workers 额外各跑一组 repeat 1 语义验证。`dense-file` 是现有密集文件预算场景名。
- 每个 `run` 在性能轮次结束后追加一轮同 workers、同 query 的 `resultVerification`，phase 为 `verification-excluded`。只有这一轮计算实际 FileResult 内容摘要，独立记录其状态、计数和哈希；性能轮次的 callback 仅记录首结果/计数/取消，不序列化 MatchItem、不保留结果、不计算内容 hash。验证轮次从性能耗时、首结果、分配和 GC 分布中排除，并且在性能轮次之后运行。验证轮次的独立 `elapsedMilliseconds` 包含 hash 开销，不是性能样本。
- `resultOrderSha256` 按完整文件发布顺序聚合；`resultContentSha256` 将文件按相对路径 Ordinal 排序后聚合。每个文件摘要包含相对 scenario root 的规范化路径（仅 hash 输入）、扩展名、mtime、所有实际 MatchItem 字段（Term/Before/Text/After/Location/SourceSegmentId/Offset）、SourceLocator 全部字段、文件内匹配顺序和 ExtractionWarnings；摘要直接使用独立验证轮的实际 FileResult，不为单个结果再次调用提取器；没有把这些原文或路径写入报告。两个摘要使用固定 32-byte 帧串接再 SHA-256。脚本要求不同 workers 与不同进程的两个摘要均等于 serial baseline。
- `workers`、benchmark assembly 的 `binarySha256`、每轮进程 CPU 时间差值、进程级 allocation/GC delta、寿命累计 peak 均保留。脚本另记录 LocalDocSearch DLL 依赖 hash，并在全部进程结束后再次核对。源文件 fingerprint 由已逐文件验证的 SHA/长度/mtime 清单及实际控制文件 stamps 得出，核对 before/after 及所有进程一致。报告不写源路径。
- `control` 只支持 many-small / mixed，依次执行 pause-resume、paused-stop、running-stop。首次扫描完成的同步进度回调且仍有剩余文件时请求控制；禁止仅靠启动后的固定延迟碰运气。暂停请求后等待 `IsPaused`，确认后观察至少 200ms，要求 scanned/matched/occurrences、完整文件结果及 issue/progress 回调计数全部冻结；然后 resume 或在暂停状态直接 cancel。运行 stop 直接在触发进度回调中 cancel，要求无后续结果。所有模式等待 SearchAsync 结束，并再观察 200ms 验证无迟到回调。任务已完成无触发机会、确认超时（10 秒）、计数未冻结均是显式失败。
- 控制报告有 pause 确认延迟、stop 到整个搜索任务结束延迟、首结果、GC delta/分配/寿命 peak、冻结前后计数及源完整性。控制模式的实际结果 hash callback 计入控制耗时/分配，和独立的性能 `run` 口径分开。同步第三方解析器仅能在协作检查点响应，控制测量没有声称抢占解析内部。独立 deterministic tests 使用自有 gate extractor 检查所有启动的提取器均退出。
- 每次脚本运行使用新的 GUID 日志目录；逐组核验进程退出码、所有 round 状态/计数/diagnostics、独立验证摘要、源完整性和控制响应，再输出各场景/worker 的 median、min/max、首结果、CPU、allocation、GC、寿命 peak。单组进程最多等待 300 秒，超时终止该进程树并使实验失败；CLI 自身始终等待其搜索任务，不遗留后台搜索。
- **保留负面结果。** large-log 只有一个 eligible file，受限文件并发预期不提速。OS cache 未清空；源 hash 前置读取会暖缓存；进程中三个 repeat 的寿命 peak 是累积高水位。禁止把这些值解释成冷缓存或每轮独立峰值。脚本只收集和校验，不断言 workers 2/4 必须更快。

