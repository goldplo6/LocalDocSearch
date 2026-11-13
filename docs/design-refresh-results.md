# 清爽专业桌面界面 rc.5 验收报告

日期：2026-11-12。状态：已完成本地候选交付。按用户批准的[独立 Demo](../artifacts/ui-design-demo/index.html)落实到 Avalonia；实施范围见[设计计划](design-refresh-plan.md)。完整回归由 rc.4 的 909 项增至 **927 项全通过，0 失败、0 跳过**，13 项目 Release 构建为 0 警告、0 错误。

## 界面与操作

- 浅灰背景、白色内容区、蓝色主搜索操作，统一正文、边框和小圆角；匹配保留加粗并加柔和黄色底色。
- 文件结果拆分文件名、匹配数、路径、时间与摘要；保留主要“定位并选中”，原文件打开、查找和所在目录等收进更多操作菜单。文件/匹配菜单仍使用各自行的数据与原命令。
- 主窗口宽度至少 1100 DIP 时按需打开右侧只读预览；关闭恢复原结果宽度。侧栏可主动弹出，主窗缩窄后将当前请求转到独立窗；窄窗中的新预览直接使用独立窗。
- 内嵌与独立预览共用来源核验组件。更换命中、关闭、切换数据源或退出时取消旧加载，以请求代次丢弃迟到返回；重新提取并核对实际来源后，读取真实 TextBox.SelectedText 与命中文字相等才报告选中。
- 高级搜索保留逻辑/正则双页、可视化/手写往返与错误阻止；实际公式集中展示，条件层级、等宽输入、就近错误、折叠帮助和固定底部操作采用统一样式。

Demo 中示意的密度选项与动画未作为新功能加入；既有设置排序继续保留。未引入新 NuGet 依赖。搜索公式、预算、离线只读语义、组合输入护栏、两层虚拟化、50 条分页及末页加载按钮占位保持原有行为。

## 完整回归

| 测试项目 | 通过 |
|---|---:|
| Core | 437 |
| Desktop | 120 |
| Extractors | 113 |
| Navigation | 191 |
| Settings | 37 |
| Benchmarks | 29 |
| 合计 | **927** |

本轮新增 18 个桌面用例：共享预览组件 5 个、主窗口与响应预览 8 个、高级搜索布局 5 个。覆盖重复词精确选区、取消及迟到返回、来源变化、数据源更换与关闭清理、菜单数据绑定、侧栏关闭/弹出/缩窄迁移、三种尺寸结果面积，以及四档缩放下高级窗口输入和底部按钮可达。此前 16 个输入交互用例与原滚动测试全部通过，详见[输入环境矩阵](input-environment-results.md)。六套实际 TRX 位于[候选测试目录](../artifacts/releases-design-refresh/LocalDocSearch-1.0.0-rc.5-win-x64/test-results)。

首轮布局回归发现展开详情顶部被外层裁切约 3 DIP；通过缩紧详情间距、保留末页按钮并把内层最大高度设为 320 修复。原完整可见、末条可达与虚拟化断言保持原样；未增加持续滚动定位。首次新增测试中的同步异常委托及未模板化隐藏控件取法也已修正，最终以冻结源码重新完成全套候选构建。

## 独立解压后的真实窗口

[包级审计](../artifacts/design-refresh-verified-86851641ca62420ba1081228aa678f63/final-audit.json)使用同一 SHA-256 的 ZIP 独立解压运行，实际 coreclr 来自包内；将共享运行时路径指向空目录。测试使用的 UIAutomation .NET 6 组件仅属工具，不是产品运行依赖。

[高级搜索实窗](../artifacts/design-search-uia-3e72ad3d79c44140b213b52fdc7948ac/results.json)通过手写 `西瓜 AND (苹果 OR 荔枝)` 得到 3 文件、7 处，精确为 truth-5/6/7；错误位置 13 阻止应用，手写/可视化往返保持结果。正则 `[A-Z]\d{2}\b` 得到 1 文件、3 处，侧栏报告 A12 位于第 1 行第 4 列；错误表达式、零长度跳过和后顾匹配均通过。

[分组实窗](../artifacts/query-grouping-uia-7279d53659034bae98a3337fd8f12a75/results.json)通过平面 5 文件/10 处 → 分组 3/7 → 嵌套 3/7 → 取消括号 5/10 → 添加重复词 OR 后 6/11，非相邻选择、取消草稿、待添加输入禁用应用及不持久保存查询均符合预期。

### 结果空间与滚动

[面积实测](../artifacts/phase-5-package-uia-a1df8d8a784a4438aeb7d1ddaff68fcb/results.json)在本机 125% DPI、未打开侧栏时记录；分母为包括窗口边框的整窗面积，和 headless 客户区指标分别保留。

| 主窗尺寸（DIP） | 关键词数 | 结果区占整窗 |
|---|---:|---:|
| 850 × 640 | 0 | 61.51% |
| 1140 × 820 | 0 | 68.18% |
| 1600 × 900 | 0 | 71.33% |
| 850 × 640 | 64 | 61.51% |

[300 文档原生滚轮](../artifacts/result-wheel-9de5c28e806e4ac1a9af073d62c41418/results.json)得到 369 处匹配，共 234 次滚轮消息，回收后 107 次到达末条。末项展开、50/70 条加载、触底、真实回收后恢复和最后匹配可见均通过；源文件未变，测试自有进程正常退出。

### 响应式预览四场景

[独立预览审计](../artifacts/design-preview-uia-a00dec03e6114adca0ecfca7a5133a75/results.json)在同一个已验解压目录运行，使用两个自有 TXT、共 5 处 PREVIEW；每次均核对只读正文和第 1 行第 7 列、长度 7 的选中反馈。

| 场景 | 实际结果 |
|---|---|
| 1140 DIP 宽窗侧栏，随后关闭 | 结果宽度 1124 → 776 → 1124 DIP |
| 侧栏显示时缩至约 850 DIP | 当前请求迁移至唯一自有独立窗；结果宽度恢复 834.4 DIP |
| 宽窗主动弹出侧栏 | 独立窗核验相同请求；结果宽度恢复 1124 DIP |
| 窄窗直接打开新请求 | 唯一自有独立窗，侧栏保持隐藏 |

所有场景均正常关闭预览与主窗，源文件哈希、长度及修改时间不变；未使用全局输入、剪贴板或外部阅读器。原生 UIA TextPattern 在该 TextBox 上未提供选区读取，实窗证据采用 ValuePattern 逐字核对正文与应用实际 SelectedText 相等后生成的定位反馈；直接 TextBox.SelectedText 的断言由桌面控件测试覆盖，未宣称原生 UIA 独立读回了选区。

## 候选与完整性

[下载 rc.5 Windows x64 自包含 ZIP](../artifacts/releases-design-refresh/LocalDocSearch-1.0.0-rc.5-win-x64/LocalDocSearch-1.0.0-rc.5-win-x64.zip)。完整解压后运行其中的 LocalDocSearch.Desktop.exe，保留周围 DLL 与运行时文件。运行时为 10.0.14。

SHA-256：`98225b5b47d687bd8545ed7e530da3698ca94afa530185c536cf20b17740f7e3`。

包级核对 162 个源码输入、13 个源依赖锁、354 个解压文件；运行前后文件集合、长度和哈希一致。67 个文本/自有 PDB 按三种工作区路径形式扫描，泄漏为 0。响应预览实测之后再由[文档与完整性终核](../artifacts/design-refresh-verified-86851641ca62420ba1081228aa678f63/document-final-audit.json)检查包、源码、脚本和链接，记录测试进程均已退出。

## 截图与边界

保留[主窗侧栏](../artifacts/design-preview-uia-a00dec03e6114adca0ecfca7a5133a75/wide-inline-selected.png)、[关闭后的主窗](../artifacts/design-preview-uia-a00dec03e6114adca0ecfca7a5133a75/wide-closed-results.png)、[可视化高级搜索](../artifacts/query-grouping-uia-7279d53659034bae98a3337fd8f12a75/grouped-editor.png)、[手写错误](../artifacts/design-search-uia-3e72ad3d79c44140b213b52fdc7948ac/manual-error.png)及[正则编辑器](../artifacts/design-search-uia-3e72ad3d79c44140b213b52fdc7948ac/regex-editor.png)供人工审阅。当前图像读取接口返回模型未声明 image input；本报告的验收基于真实控件、值、几何及行为，没有作像素级视觉审图结论。

本次交付本地 rc.5，保留旧候选。系统中文输入法、原生跨屏 DPI、干净机/无 Office、真实 360 与完整阅读器矩阵仍按环境矩阵验收；正式分发另行收尾。原文件自动填词维持停止调试，代码未提交或推送，未签名或公开发布。
