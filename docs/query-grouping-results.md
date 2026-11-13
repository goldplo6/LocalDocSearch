# 可视化括号分组与 rc.3 验收

日期：2026-11-12。状态：本阶段已完成，Windows x64 自包含本地候选 **1.0.0-rc.3** 已构建、独立解压并通过真实窗口验收。此前 rc.2 保留；本报告不代表公开正式版、签名、干净机或完整环境矩阵验收。

## 使用方法与交付内容

1. 逐词添加关键词后，点击顶部“括号分组…”。关键词输入保持普通文字语义，输入框中的 AND、OR 和括号也按文字搜索。
2. 勾选同层相邻项目，点“组成 AND 组”或“组成 OR 组”。缩进表示父子层级；单选一个组可继续嵌套或“取消所选括号”。取消括号后，按父层现有连接及 AND 优先于 OR 重新解释，请核对实时预览。
3. 每项前的下拉框修改同层连接；可删除关键词或整组，空组自动清除。新增词可加入根层或所选组；重复词保留独立编辑位置。
4. 点“应用公式”提交主查询，再点“开始搜索”。取消或 Esc 保留原查询；尚未添加的输入会阻止应用，搜索期间阻止修改。结构查询的连接、删除通过编辑器维护，普通输入可继续向根层追加。

预览和搜索引擎使用同一棵表达式树。公式决定整文件是否合格，合格文件中各关键词的实际命中都会展示。重复文字映射回独立词位置，相同文字的命中只提取、计数一次。关键词、表达式树、正文和结果均未新增持久化；偏好仍可保存搜索根目录等原有设置。

实现入口：[表达式节点](../src/LocalDocSearch.Core/SearchExpression.cs)、[验证与公式](../src/LocalDocSearch.Core/SearchFormula.cs)、[搜索引擎](../src/LocalDocSearch.Core/TextSearchEngine.cs)、[草稿模型](../src/LocalDocSearch.Desktop/ViewModels/QueryEditorModel.cs)、[编辑器状态](../src/LocalDocSearch.Desktop/ViewModels/QueryEditorViewModel.cs)、[实际窗口](../src/LocalDocSearch.Desktop/QueryGroupingWindow.axaml)。

## 核心边界与回归

- 旧相邻连接请求继续转换为树，保留旧构造与格式；显式树和相邻连接同时提交时报错。字面量、大小写、跨片段整文件判断、只读提取、预算和取消语义保留。
- 在文件访问前完成一次迭代验证、快照和后序指令生成；每个文件只执行已生成指令。最多 128 层、256 个节点，每个原始输入位置恰好出现一次；词数与字长仍受查询预算约束。
- 无效节点、空子节点、无效运算符、越界、重复或遗漏位置，以及过深、过大、循环等结构在访问文件前报错。格式化仅递归处理已验证的有界快照。
- 真实控件回归发现并修正连接选择/卸载时重建列表的重入问题：有效连接原位更新，忽略控件生命周期的 -1 值，结构变化才重建行。测试保留实际 ComboBox 操作，并检查行身份和选中连接；待输入保护的断言保留。

独立白名单源码快照构建 **13 个项目，0 警告、0 错误**。六项目真实 TRX 合计 **752/752 通过，0 失败、0 跳过**，比 rc.2 新增 46 项核心分组和 17 项桌面编辑器回归。

| 测试项目 | 通过 |
|---|---:|
| Core | 309 |
| Desktop | 73 |
| Extractors | 113 |
| Navigation | 191 |
| Settings | 37 |
| Benchmarks | 29 |
| 合计 | 752 |

[候选 TRX 目录](../artifacts/releases-query-grouping/LocalDocSearch-1.0.0-rc.3-win-x64/test-results/)；新增专项测试：[核心](../tests/LocalDocSearch.Core.Tests/GroupedExpressionTests.cs)、[模型](../tests/LocalDocSearch.Desktop.Tests/QueryGroupingModelTests.cs)、[真实 Avalonia 控件](../tests/LocalDocSearch.Desktop.Tests/QueryGroupingControlTests.cs)。

## 独立解压后的真实窗口

在自有测试进程中用 8 个原创 UTF-8 真值表文件、独立偏好文件执行，应用实际加载解压包内的 coreclr；DOTNET_ROOT 指向不存在的测试目录并关闭多级查找。以下是实际点击编辑器、应用公式、开始搜索后的结果，不只是调用模型求值。

| 操作/公式 | 合格文件 | 实际匹配 |
|---|---:|---:|
| 初始 `(西瓜 AND 苹果) OR 荔枝` | 5 | 10 |
| 将苹果、荔枝组成 OR 组：`西瓜 AND (苹果 OR 荔枝)` | 3 | 7 |
| 再嵌套：`西瓜 AND ((苹果 OR 荔枝))` | 3 | 7 |
| 取消两层括号，恢复初始公式 | 5 | 10 |
| 根层 OR 添加重复西瓜：`(西瓜 AND 苹果) OR 荔枝 OR 西瓜` | 6 | 11 |

分组结果另核对了具体文件集合，恰好为真值表编号 5、6、7。非相邻项目分组给出准确反馈且保持公式；`AND (literal)` 保留为一个带引号显示的字面量；删除及新增后取消草稿保持主查询；未添加输入阻止应用。原始语料的哈希、长度和修改时间不变，偏好中没有关键词或查询结构，测试进程正常关闭，未操作剪贴板。

[分组原生报告](../artifacts/query-grouping-uia-9dcba7ca6de34b0eaff5317309673913/results.json)；[编辑器截图](../artifacts/query-grouping-uia-9dcba7ca6de34b0eaff5317309673913/grouped-editor.png)；[分组结果截图](../artifacts/query-grouping-uia-9dcba7ca6de34b0eaff5317309673913/grouped-results.png)。截图由自有 HWND 采集；当前模型图像通道未开放，未将截图记为已做视觉复核，操作、控件值及几何数据由 UIA 核验。

首轮验收工具只在桌面的直接子窗口中寻找模态编辑器，因实际编辑器挂在主窗口的自动化子树中而超时。工具已按自有主窗口子树定位，并等待模态关闭后退出主窗口；同一 ZIP SHA256 随后完整通过。首轮自有进程已取消对话框并正常关闭，保留[首轮失败记录](../artifacts/query-grouping-uia-1a062c4a6bf64ce2bb86eef01bdc776e/results.json)以说明证据来源。

## 原查询、紧凑布局与滚轮回归

旧混合公式在原四文件语料仍为 3 文件/5 匹配，切换既有连接后为 4 文件/6 匹配；修改“下一连接”不回改已经保存的连接。120 DPI（125%）下，850×640、1140×820、1600×900 三个窗口的结果区占整窗面积分别为 **64.36%、70.55%、73.51%**；850×640 添加 64 词后仍为 **64.36%**。见[布局与旧公式报告](../artifacts/phase-5-package-uia-a9dc95628adc43488eb9a2fda07925f8/results.json)。

同一解压包通过 **300 文档、369 匹配**的原生滚轮回归：246 次自有窗口滚轮消息，首批第 50 处及全部第 70 处完整可见；触底保持展开。滚离使末项真实回收后，再进入仍展开并能到达第 70 处；回收后的滚轮使用 113 次，预算 160 次。源文件不变、测试进程正常关闭、未操作剪贴板。见[实际滚轮报告](../artifacts/result-wheel-d173452b760041ef988179384125643a/results.json)。原有 50 条分页、内外虚拟化和万文件容器上限测试也包含在完整桌面回归中。

## 候选与最终核验

[Windows x64 自包含 ZIP](../artifacts/releases-query-grouping/LocalDocSearch-1.0.0-rc.3-win-x64/LocalDocSearch-1.0.0-rc.3-win-x64.zip)，SDK 10.0.403、随包运行时 10.0.14。保留普通目录包及原生依赖，不裁剪、不使用 AOT、单文件或压壳。

```text
SHA256  9fe13cce2a62a5809539eae090a795dbd04aaeeabf9e9ce9836254070f7ffc60
```

独立核验 353 个清单条目和清单本身，解压目录共 **354 文件**；实际运行前后文件集合、长度和哈希一致。**151 个源码输入、13 个源锁文件**与构建记录一致；扫描 67 个文本/自有 PDB 文件的三种工作区路径形式，0 命中。全部自有验收进程正常退出。实际运行时模块来自包内，无新增产品对 UIA 工具所用 .NET 6 的依赖。

[最终机器审计](../artifacts/query-grouping-verified-96587dbec5a744c4b2226ae791a9e1bb/final-audit.json)；[文档链接与交付一致性终核](../artifacts/query-grouping-verified-96587dbec5a744c4b2226ae791a9e1bb/document-final-audit.json)；[解压验收脚本](../artifacts/verify-query-grouping-package.ps1)；[分组窗口探针](../artifacts/verify-query-grouping-uia.ps1)。

## 保留的后续事项

中文 IME、完整键盘与多 DPI 矩阵、干净 Windows/无 Office、真实杀毒环境、目标阅读器与 Office 版本矩阵、签名及公开发布继续分别验收。原文件自动填词维持停止调试；本轮没有启动 Acrobat 或更改阅读器、360、Hosts、服务设置。索引、跨次正文缓存和 OCR 未纳入本阶段；工程预算不是进程整体内存硬限，共享修改的文件也不是一致性快照。未执行 commit、push 或远程发布。

完整计划见[实施计划](query-grouping-plan.md)，搜索机制与后续顺序见[原理与后续计划](search-principles-and-next-steps.md)。
