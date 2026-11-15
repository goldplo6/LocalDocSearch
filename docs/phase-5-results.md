# 第五阶段结果：查询公式、原文件动作与本地候选

日期：2026-11-10。状态：本地候选与解压运行验收完成；默认 Acrobat 自动填词尚未通过实机验收。本文明确区分实际通过、条件未齐和公开发布关口。

## 1. 本轮反馈

- 查询连接按每个后续关键词保存；下一连接选择不会回改旧连接，词条间连接可单独切换。AND 优先于 OR，顶部带必要括号的公式与引擎求值一致。单个关键词内的 AND、OR、括号和空白仍是字面量。
- 待添加词参与有效公式预览；达到关键词上限时显示上限，不虚显未接受的词，也不静默忽略它继续搜索。关键词、连接和结果不写入偏好文件。
- 原文件查找入口传递实际命中文本；首条与逐条匹配分别可用。查找辅助的 `SearchFieldFilled / ManualFindRequired / Failed` 与原生 `Exact / Section / OpenedOnly / Failed` 分开，填框读回不代表精确原文选区。当前受控适配集中在 PDF 查看器；其他程序显示手工查找说明。
- 所在目录改用 Windows Shell PIDL，移除了 explorer.exe 命令行拼装及 PATH 解析。

## 2. 原文件动作的真实证据

目录选择已通过实机：公共 `WindowsContainingFolderRevealer.RevealAsync` 返回成功，独立 Shell.Application 的 SelectedItems 读回确认自有 PDF 被选中。源文件哈希和修改时间保持不变。证据：[shell-result.json](../artifacts/history/navigation/nav-original-find-smoke-65b1/shell-result.json)。这不证明 360 不再提示；未更改 360、排除项或系统文件关联。

本机默认 Acrobat 的自动填词尚未通过实机验收。完整 JavaScript 文档路径已核验，但首次 Adobe General Terms Of Use 条款模态使原窗口禁用，UIA 找到的编辑框数为 0；前台也未成为已核验目标。因此助手返回 ManualFindRequired，没有发送 Find 或填词，也没有向正文或其他应用输入。未代用户接受条款。

证据：[冷启动结果](../artifacts/history/navigation/nav-original-find-smoke-65b1/first-result.json)、[热重试结果](../artifacts/history/navigation/nav-original-find-smoke-65b1/warm-result.json)、[阶段诊断](../artifacts/history/navigation/nav-original-find-smoke-65b1/final-diagnostics.json)、[自有模态 OCR](../artifacts/history/logs/nav-owned-modal-ocr-evidence-65b1.txt)、[清理记录](../artifacts/history/navigation/nav-original-find-smoke-65b1/cleanup-result.json)。截图已保存，当前模型的图片读取接口未声明图像输入，未据此声称人工视觉审阅。测试 OCR 仅用于诊断这张自有弹窗截图，不属于产品功能。

自动助手不使用个人剪贴板。只有用户主动点击“复制查找词”才写入剪贴板。阻塞的外部自动化工作由静态单工门闩限制，超时或取消后也需真实工作退出才释放，避免连续请求堆积。目标路径、窗口、焦点、编辑框身份或读回证据不符时保留手工步骤。自有 PDF 经完整路径再次核验后 Close(1) 返回 true，源哈希和时间不变；现有用户程序保持原状。

## 3. 构建和回归

首轮隔离快照 Release 构建：13 项目、0 警告、0 错误。六份真实 TRX 独立核对，共 683 通过、0 失败、0 跳过。

| 测试项目 | 通过 |
|---|---:|
| Core | 263 |
| Extractors | 113 |
| Settings | 37 |
| Navigation | 191 |
| Desktop | 50 |
| Benchmarks | 29 |
| 合计 | 683 |

新增公式覆盖 AND 优先级、重复/大小写映射、文件级跨段、字面量、连接快照和输入上限。Navigation 包含目标变化、正文控件、只读、焦点、读回、取消、PIDL 释放以及超时/取消的阻塞工作测试。512 组合断言属于测试内部断言，没有虚增测试用例数。

首轮候选在 publish 和依赖许可证验证后，因 OrderedDictionary 的 Measure-Object 属性绑定失败而停止；没有把它标为可分发候选。原目录 `artifacts/releases/LocalDocSearch-1.0.0-rc.1-win-x64` 保留失败证据。修复使用显式整数累加和 List.ToArray()，加入六项目 683 条真实 TRX JSON 往返及单项目数组形状回归，脚本检查 [7/7 结果](../artifacts/history/builds/release-script-tests/d0826ac356c249728a8584c9ade8857a/results.json) 全通过。

## 4. 本地候选与解压验收

第二次完整流程退出码 0：13 项目 Release 构建无警告/错误，六项目 683/683 再次通过，源锁与候选 RID 锁、发布文件、运行时许可证、文件清单和 ZIP 独立校验均通过。使用独立 `artifacts/releases-validated` 输出根，首次失败目录完整保留。

- `LocalDocSearch-1.0.0-rc.1-win-x64.zip`（原路径：`../artifacts/releases-validated/LocalDocSearch-1.0.0-rc.1-win-x64/LocalDocSearch-1.0.0-rc.1-win-x64.zip`；历史包已清理；当前版本见 [rc10 优化报告](fixed-overhead-results.md)）
- [SHA256SUMS](../artifacts/history/releases/releases-validated/LocalDocSearch-1.0.0-rc.1-win-x64/SHA256SUMS.txt)
- 构建与六项目测试记录（原路径：`../artifacts/releases-validated/LocalDocSearch-1.0.0-rc.1-win-x64/LocalDocSearch-1.0.0-rc.1-win-x64/build-record.json`；包内记录已随历史包清理）
- 353 条文件哈希清单（原路径：`../artifacts/releases-validated/LocalDocSearch-1.0.0-rc.1-win-x64/LocalDocSearch-1.0.0-rc.1-win-x64/file-manifest.json`；包内记录已随历史包清理）；加清单本身共 354 文件
- 实际分发依赖与运行时许可证据（原路径：`../artifacts/releases-validated/LocalDocSearch-1.0.0-rc.1-win-x64/LocalDocSearch-1.0.0-rc.1-win-x64/docs/dependency-licenses/inventory.json`；包内记录已随历史包清理）

ZIP SHA-256：

```text
af47428341171602cf2b2fabc4b2212fb09090b0da6f641fe59c5ef887abc333
```

从该 ZIP 解压至独有目录，逐文件校验哈希/长度，确认没有额外文件。将 DOTNET_ROOT 和 DOTNET_ROOT_X64 指向不存在的测试目录，并以 DOTNET_MULTILEVEL_LOOKUP=0 启动实际解压 exe，进程模块读回确认加载同目录 coreclr.dll（10.0.14）。这是当前机器的自带运行时验证，不是真正干净机器验收。证据：[解压完整性](../artifacts/history/builds/phase-5-unpacked-a77046958e0c4898aa5a70ec556443f0/extraction-verification.json)、[真实窗口结果](../artifacts/history/search/phase-5-package-uia-222310967fbf42e1a65afcdb3516cb04/results.json)。

真实窗口中，四份原创 TXT 对 `(西瓜 AND 苹果) OR 荔枝` 显示 3 文件、5 匹配；点击既有 AND 连接改成 OR 后为 4 文件、6 匹配。切换“下一连接”没有回改已存连接。64 个长关键词没有增加工具栏高度。

| 窗口设定（DIP） | 关键词数 | 结果 / 整窗外框面积 |
|---|---:|---:|
| 850 × 640 | 0 | 64.36% |
| 1140 × 820 | 0 | 70.55% |
| 1600 × 900 | 0 | 73.51% |
| 850 × 640 | 64 | 64.36% |

本机 DPI=120（125%）。四组均超过 60%，不是仅按客户区计算。已保存自有窗口截图；公式和结果通过 UIA 读回，未声称图片视觉审阅。自有测试进程已正常关闭，四份语料保持不变，无剪贴板操作。

[运行后最终审计](../artifacts/history/search/phase-5-package-uia-222310967fbf42e1a65afcdb3516cb04/package-final-audit.json) 再次核对全部 353 条清单哈希及 354 文件计数：程序运行没有改写分发目录；142 个当前源码输入与构建记录一致，13 份源锁保留。67 个文本/自有 PDB 文件以 Windows、斜杠和 JSON 转义三种工作区路径形式扫描，未检出该工作区路径。

## 5. 分发约定与剩余关口

项目采用 MIT；30 个实际 NuGet 包保留精确版本、包哈希和上游许可证/NOTICE，自带 .NET 运行时单独记录许可证和原始包证据。自带 win-x64 运行时、无裁剪、无 Native AOT、无单文件压缩或 UPX；白名单源码快照隔离构建，源锁先 locked 验证，候选 RID 锁只在快照生成并再次 locked 验证。未声称跨干净环境字节完全一致。

公开仓库、远程发布、签名、真正干净 Windows/无 Office 环境、真实 360 检测、完整 IME/键盘/多 DPI 和截图视觉验收仍各自保留。原文件自动填词需要可用且已核验的查看器窗口；本机 Acrobat 的首次条款处理后需重做该项真实读回。索引、缓存、OCR 和安装程序没有纳入本阶段产品。路线图见 [第五阶段计划](phase-5-plan.md)，发布操作见 [发布指南](release-guide.md)。
