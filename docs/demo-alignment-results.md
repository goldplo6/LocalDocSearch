# Demo 对齐与应用图标 rc.6 验收报告

日期：2026-11-13。状态：已完成本地候选交付。依据用户对 rc.5 与批准 Demo 差距的反馈，本轮重新调整正式 Avalonia 控件与布局，并设计、接入原创应用图标。完整回归 **938/938 通过，0 失败、0 跳过**；13 个项目构建为 **0 警告、0 错误**。同一 ZIP 独立解压后，图标资源、高级搜索、结果面积、300 文档滚轮及响应式预览均通过真实窗口核验。

## 1. 本轮对照与修改

视觉依据为[原批准 Demo](../artifacts/ui-design-demo/index.html)。继续采用白色表面、浅灰背景 `#F4F6FA`、蓝色强调 `#315CE7`、细边框与圆角，重点调整页面结构和控件外观：

| 区域 | rc.6 正式实现 |
|---|---|
| 品牌栏 | 独立白底区域，原创文档放大镜标识、产品名、离线提示及帮助/设置入口 |
| 搜索卡片 | 目录、关键词、连接、公式与主搜索操作分层；搜索设置和低频说明仍从原入口进入 |
| 结果工具栏 | 独立标题与真实文件计数，和搜索表单、结果卡片分开 |
| 文件卡片 | 格式徽标、单个展开箭头、文件名、匹配数、路径、时间与摘要；不同文档类型具有不同徽标颜色 |
| 展开详情 | 真正的 Expander 局部模板，保留双层虚拟化、50 条加载、展开状态与滚轮修复 |
| 主操作 | 扁平按钮模板、明确蓝色主按钮、图形预览入口；真实控件与无障碍名称继续保留 |
| 高级搜索 | 真正的双页 TabControl、蓝色下划线、可视化/手写分段切换、公式辅助、编辑器标题区、帮助区和固定页脚 |
| 只读预览 | 标题、来源信息、白色正文及底部状态分区；宽窗侧栏、窄窗独立预览与关闭恢复保持原行为 |
| 应用图标 | 同一原创蓝色文档放大镜用于 EXE、窗口与品牌栏；ICO 包含 9 个尺寸 |

主要实现：[主窗口](../src/LocalDocSearch.Desktop/MainWindow.axaml)、[高级窗口](../src/LocalDocSearch.Desktop/QueryGroupingWindow.axaml)、[共享主题](../src/LocalDocSearch.Desktop/App.axaml)、[预览控件](../src/LocalDocSearch.Desktop/Controls/MatchPreviewPane.cs)、[BrandMark](../src/LocalDocSearch.Desktop/Controls/BrandMark.axaml)。最小窗口采用紧凑间距，维持结果区域占整窗至少 60% 的既有要求；原生标题栏与小窗口布局不按 HTML 像素逐项等同。

## 2. 图标设计与接入

构图为蓝色圆角底板、白色折角文档和放大镜，表达“在本地文档中查找”。[图标说明](app-icon-design.md)记录颜色、构图、同源生成与验证方法；可直接查看[512 PNG](../src/LocalDocSearch.Desktop/Assets/app-icon-512.png)、[SVG](../src/LocalDocSearch.Desktop/Assets/app-icon.svg)和[浅色/深色尺寸接触表](../src/LocalDocSearch.Desktop/Assets/app-icon-preview.png)。

ICO 的尺寸为 **16、20、24、32、40、48、64、128、256** 像素。已核对 PNG 签名、CRC、尺寸、透明度、ICO 目录及 Windows 解码。独立解压 EXE 的 `RT_GROUP_ICON #32512` 九帧与源 ICO 的尺寸、PNG SHA-256 全部一致，重建 ICO 的 Windows 解码通过，见[包内 PE 图标报告](../artifacts/demo-icon-pe-36b9b20f708a41ab8d973fa11c327b66/results.json)。[生成器](../scripts/generate-app-icon.ps1)仅依赖 Windows System.Drawing，不新增 NuGet 或运行时依赖。

## 3. 自动化回归

| 测试项目 | 通过 |
|---|---:|
| Core | 437 |
| Desktop | 131 |
| Extractors | 113 |
| Navigation | 191 |
| Settings | 37 |
| Benchmarks | 29 |
| 合计 | **938** |

相对 rc.5 新增 **11 项桌面用例**：[主窗口对齐测试](../tests/LocalDocSearch.Desktop.Tests/DemoAlignmentMainTests.cs)6 项覆盖独立区域顺序、64 词公式、结果面积、品牌资源、类型徽标及真实箭头展开；[高级窗口对齐测试](../tests/LocalDocSearch.Desktop.Tests/DemoAlignmentAdvancedTests.cs)5 项覆盖真 tabs、下划线、模式守卫、编辑器/页脚以及四档缩放。原有 120 项桌面用例全部保留并通过，包含输入法预编辑、Enter/Esc/Tab、预览生命周期、10,000 文件虚拟化和末项滚动回归。六份[完整 TRX](../artifacts/releases-demo-alignment/LocalDocSearch-1.0.0-rc.6-win-x64/test-results/)与[构建记录](../artifacts/releases-demo-alignment/LocalDocSearch-1.0.0-rc.6-win-x64/LocalDocSearch-1.0.0-rc.6-win-x64/build-record.json)均保留。

新增检查曾遇到 headless Bitmap 固定 1×1、125% 布局物理像素取整，以及 PE 工具循环变量类型约束三个测试基础设施问题；分别改为读取嵌入 PNG 的真实 IHDR、允许一个物理像素布局误差和更正工具变量名。结果区域 60% 阈值与旧测试断言保持原样，产品资产及搜索行为未因此改动。最终发布流程重新执行全部 938 项。

## 4. 独立解压后的真实窗口

使用 ZIP 的全新解压目录运行同一 EXE，文件 SHA-256 为 `3d6a39bc0182649d12a8d7c78427fbd9169e8aee2ad2bfbde883921b3ca72ae6`。应用实际加载解压目录中的 `coreclr.dll`，运行时为 10.0.14；UIA 探针所用 .NET 6 仅属于本机验证工具，不是应用依赖。

- [高级搜索报告](../artifacts/design-search-uia-2c2811e28e5547bb9ff01a444a87f5ef/results.json)：手写 `西瓜 AND (苹果 OR 荔枝)` 得 3 文件/7 处；错误公式阻止应用，可视化往返结果一致；正则真实命中、预览、错误阻止、零长跳过和后行断言通过。
- [分组报告](../artifacts/query-grouping-uia-44d8cf2a25494d11ae69be7c88e9719d/results.json)：平式 5/10 → 分组 3/7 → 嵌套 3/7 → 取消括号 5/10 → 重复词 6/11，保留非相邻、取消和待添加输入守卫。
- [面积与原查询报告](../artifacts/phase-5-package-uia-761079d967b541519f7156ebd92dcb9a/results.json)：本机 125% DPI，未开预览，850×640、1140×820、1600×900 及 850×640/64 词的结果区占整窗分别为 **63.32%、61.32%、65.26%、63.32%**。分母为实际原生整窗，不是仅内容区。
- [滚轮报告](../artifacts/result-wheel-2a62c09d4699495292f5203d551427e9/results.json)：300 文档/369 处，166 次原生滚轮消息；末文档 70 处，首批第 50 条和加载后第 70 条均完整可见。触底仍展开，真实滚离回收后恢复展开，再用 73 次滚轮到末条。源文件不变，自有窗口正常关闭。
- [响应预览报告](../artifacts/design-preview-uia-57580da6801540fba1c01e1f6601becd/results.json)：宽窗侧栏打开/关闭、缩窄迁移、主动弹出和窄窗直开四场景通过。结果宽度为 **1100 → 752 → 1100 DIP**；迁移后预览内容与命中状态保持一致，自有预览全部关闭。

预览核验采用只读 ValuePattern 全文与应用内部 `SelectedText` 相等后返回的行/列/长度状态；当前 UIA `TextPattern` 不可用，未将其描述成独立 UIA 选区证据。直接 TextBox.SelectedText 另由桌面控件测试覆盖。所有探针只操作自己的测试进程与原创文档，不调用外部阅读器或自动写剪贴板。

本轮截图：[主窗口及侧栏](../artifacts/design-preview-uia-57580da6801540fba1c01e1f6601becd/wide-inline-selected.png)、[关闭侧栏后的结果](../artifacts/design-preview-uia-57580da6801540fba1c01e1f6601becd/wide-closed-results.png)、[手写/可视化页](../artifacts/design-search-uia-2c2811e28e5547bb9ff01a444a87f5ef/manual-visual-roundtrip.png)、[正则页](../artifacts/design-search-uia-2c2811e28e5547bb9ff01a444a87f5ef/regex-editor.png)。图片读取工具仍返回模型未声明图像输入，截图已保留，未宣称完成像素视觉审图。

## 5. 本地成品与最终核对

- [rc.6 完整 ZIP](../artifacts/releases-demo-alignment/LocalDocSearch-1.0.0-rc.6-win-x64/LocalDocSearch-1.0.0-rc.6-win-x64.zip)。完整解压并保留所有伴随文件。
- [本机可运行 EXE](../artifacts/releases-demo-alignment/LocalDocSearch-1.0.0-rc.6-win-x64/LocalDocSearch-1.0.0-rc.6-win-x64/LocalDocSearch.Desktop.exe)。旧 rc.5 继续保留，新旧目录分开。
- ZIP SHA-256：`dbe35ad571abf133b22c683c5e908033695c8719446772483c7441f8456d9305`。
- [独立包审计](../artifacts/demo-alignment-verified-f96273489bbb4ea4bc5b23de24d7c425/final-audit.json)：353 个清单项加清单自身共 354 个文件，运行前后内容和文件集一致；171 个源输入及 13 个源锁文件一致，67 个文本/自有 PDB 按三种路径形式扫描，工作区路径泄漏为 0。
- [最终文档/成品核对](../artifacts/demo-alignment-verified-f96273489bbb4ea4bc5b23de24d7c425/document-final-audit.json)重新检查本文、README、图标说明、计划、路线图、包内说明、源码/锁哈希、ZIP、全部文件清单和探针版本。

这是本地 release candidate。系统中文 IME、原生多屏 DPI、干净 Windows/无 Office、真实杀毒环境、签名及正式分发仍按[环境矩阵](input-environment-results.md)单独验收。没有本轮公开发布；原文件自动填词继续停止调试。搜索保持离线、只读、无 OCR、无索引和跨次全文缓存，缓存与索引仍按性能测量另行决策。
