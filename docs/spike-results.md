# 第一阶段验证记录

日期：2026-11-06。结论：文本搜索原型、可复现构建与自动验证已完成，实际 Windows 窗口已启动并执行原创夹具搜索。正式 UI 基线仍等待中文 IME、多 DPI 和人工交互关口；Office 精确定位实验尚未执行。

## 交付状态

| 工作包 | 状态 | 证据 |
|---|---|---|
| P1-01 工具链 | 完成 | 本地固定 SDK、官方 SHA-512 校验、中央依赖、四个包锁文件；Locked Release 构建成功 |
| P1-02 工程与契约 | 完成 | Desktop/Core/Core.Tests/Desktop.Tests；Core 不引用 UI、Office 或第三方包 |
| P1-03 文本管线 | 完成并通过回归 | TXT/MD/LOG、递归扫描、严格解码、字面量、文件级组合、全量匹配、取消及异常隔离 |
| P1-04 桌面原型 | 已实现并进行自动/真实窗口验证 | 目录、词标签、AND/OR、类型过滤、文件分组、展开行列与高亮、分批匹配、取消、错误列表 |
| P1-05 自动验证 | 通过 | 39 项 Core + 5 项 Desktop，全部通过，跳过 0 项；最终构建 0 警告、0 错误 |
| P1-06 交互与定位准备 | 清单与环境准备完成，人工验收待执行 | 三种 Office ProgID 已注册；仅表示注册存在，尚无 COM 定位证据 |

[计划与退出关口](phase-1-plan.md)和[技术决策](decision-log.md)为当前执行依据。历史规划保留原文，并增加了当前 Avalonia 路线说明。

## 环境与依赖

- Windows 11 x64，系统版本 10.0.26200；PowerShell 7.6.9。
- 机器原来只有 .NET 运行时，本轮将 SDK 10.0.403 安装到项目 `.tools/dotnet`，未修改系统 PATH。
- SDK 来自微软官方 10.0 release metadata 中的 win-x64 ZIP，已比较 SHA-512 后解压。构建脚本使用本地 SDK 并关闭 CLI 遥测。
- Avalonia、Desktop、Fluent、Headless.XUnit 均为 12.1.5；CommunityToolkit.Mvvm 8.4.2。
- Core 测试 xUnit 2.9.3；Desktop 测试 xunit.v3 3.2.2；测试适配器 4.0.1、Microsoft.NET.Test.Sdk 18.11.0。Desktop 依 Avalonia 测试包的实际依赖使用 v3 可执行宿主。
- [中央依赖配置](../Directory.Packages.props)与四项目的锁文件固定解析结果；[完整直接/传递依赖清单](../artifacts/package-inventory.json)为本机开发产物。

以上是本轮本机验证版本，未作其他操作系统或其他包版本的兼容性承诺。

## 自动化结果

最终命令：

```powershell
./scripts/dev.ps1 -Action Test -Locked
./scripts/dev.ps1 -Action Build -Locked
```

最终测试于 2026-11-06 17:23 本机时间执行：

| 程序集 | 通过 | 失败 | 跳过 | 测试执行时间 |
|---|---:|---:|---:|---:|
| Core.Tests | 39 | 0 | 0 | 322 ms |
| Desktop.Tests | 5 | 0 | 0 | 886 ms |
| 合计 | 44 | 0 | 0 | 不作为产品性能基线 |

- [Core TRX](../artifacts/test-results/29294_ORNATETEACOASTE_2026-11-06_17_23_00_net10.0.trx)
- [Desktop TRX](../artifacts/test-results/29294_ORNATETEACOASTE_2026-11-06_17_23_03_net10.0.trx)

Core 测试覆盖中文短词与包含关系、普通符号、大小写、关键词去重、每词非重叠计数、跨行文件级 AND/OR、类型过滤、六种 Unicode 编码、非法及晚出现的解码错误、UTF-16 列号、读取缓冲边界、输入错误、取消、共享/独占文件处理，以及源文件 SHA-256 和修改时间保持不变。

Desktop 测试覆盖：

1. 实例化实际窗口，加载一万条模拟结果；首屏可视文件项少于 200，调用列表 ScrollIntoView 到第 10,000 项后该容器存在，回收后可视项仍少于 200。
2. 高亮同时保留前后文、匹配粗体和括号标记。
3. 123 处匹配按 50/100/123 分批展示，总数保持 123。
4. ViewModel 对中文跨行 AND 返回正确结果，并报告未选文件类型。
5. 构造 350 条已收到错误的确定性积压，在首批错误展示时取消；全部错误最终可见，队列为空，取消后不再增加结果，状态为已取消。

Headless 是控件和逻辑测试，不替代输入法或真实滚动手感验收。

## 真实 Windows 窗口实验

通过项目本地 dotnet 启动 Release 桌面程序集，进程创建标题为“LocalDocSearch · 第一阶段原型”的真实窗口。使用 Windows UI Automation 在该窗口内填写目录、添加中文词和点击命令；没有使用系统剪贴板注入词语。

在[原创中文样本](../tests/fixtures/text/中文示例.txt)、[原创 MD 样本](../tests/fixtures/text/仅苹果.md)、[原创 LOG 样本](../tests/fixtures/text/运行示例.log)所在目录执行：

| 操作 | 实际观察 |
|---|---|
| 添加“西瓜”“苹果”，默认 AND | 扫描 3 个文件，展示 2 个文件、6 处匹配、跳过/失败 0 |
| 展开中文文本结果 | 4 处匹配；行 1 列 4、行 2 列 7、行 4 列 6 与列 9；读取到“我喜欢【西瓜】汁。”等原文上下文 |
| 切换 OR 并重新搜索 | 扫描 3 个文件，展示 3 个文件、7 处匹配、跳过/失败 0 |
| 加载一万条模拟结果 | 桌面状态明确标识模拟数据；辅助功能树中只有 6 个可视文件项 |
| 退出 | 已请求正常关闭，确认本次测试进程已结束 |

保存的辅助功能快照：[AND](../artifacts/desktop-uia-smoke.json)、[OR](../artifacts/desktop-uia-or.json)、[万条模拟列表](../artifacts/desktop-uia-demo.json)。本机还采集了[初始窗口截图](../artifacts/desktop-smoke.png)，尺寸 1443 × 1072；该截图尚未完成视觉审阅，控件文本与搜索行为证据来自上述 UI Automation 操作。

真实窗口末端滚动的辅助功能探针未取得有效结果：ScrollPattern 暴露的滚动百分比为 -1，列表 SetFocus 调用也未成功。因此未把该探针记为滚动验收通过；首末项回收由 Headless 实验验证，真实鼠标/键盘滚动继续待人工确认。UI Automation 填入中文不等于使用中文 IME 候选确认。

## 本轮发现并修复的问题

- Avalonia 12 的剪贴板 SetTextAsync 为扩展方法，补充其命名空间后编译通过。
- ListBox 上旧内容对齐属性不适用，调整为 ListBoxItem 的局部样式；TextBox 使用当前 PlaceholderText 属性。
- 桌面测试最初混用 xUnit v2 工程与 v3 AvaloniaFact，测试发现失败；改为 v3 可执行测试宿主，随后五项测试被正常发现并通过。
- 取消时原收尾逻辑只展示一批积压错误，其余会在下一次搜索时被清空。已使取消仅丢弃未展示结果，并继续分批排空已收到错误；350 条积压回归通过。
- 用户在核心扫描完成、UI 结果队列尚未展示完时取消，最终状态以取消令牌为准，避免误标“已完成”。

## 明确保留的待测与边界

- 中文 IME 候选确认与 Enter 交互，100%/150%/200% DPI、长路径布局和完整键盘导航。
- 一万条模拟列表的真实鼠标/键盘滚动手感、展开状态与延迟；未建立真实大目录扫描的耗时或内存基线。
- 巨长单行、单文件海量命中、展开全部命中后的控件量、海量错误列表内存仍没有硬上界。
- GB18030 尚未支持。共享变动文件读取不是一致性快照；列号按 UTF-16 代码单元计算。
- 重解析点策略已有实现，真实链接/联接目录与权限组合仍需专项夹具。
- CSV、DOCX、XLSX、PPTX、文字型 PDF 提取与统一 TextSegment/SourceLocator 尚未实施。
- Word/Excel/PowerPoint 注册存在已确认；实际创建 COM 实例、打开原创文档、精确选中来源位置与回退尚未实验。
- 文件默认打开、所在目录和复制路径已实现，尚未逐一进行真实外部程序/剪贴板交互验收。
- 尚无设置持久化、索引、全文缓存、OCR、安装包或自包含发布验收。
- 项目许可证 MIT/Apache-2.0 待决定；已固定包并导出全部依赖，完整逐项许可审查及公开发布准备仍待完成。

## 后续执行顺序

先按[人工清单](phase-1-plan.md)验证 IME、缩放、键盘和真实大列表交互，确认 Avalonia 正式基线；随后建立八格式原创样本、统一来源定位模型，实施 Open XML/PDF 提取和 Excel 值语义实验。Office 定位继续隔离为 Windows 模块，记录 Exact/Section/OpenedOnly/Failed，不把仅打开文件记作定位成功。
