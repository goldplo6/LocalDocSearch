# LocalDocSearch 应用图标

## 构图与视觉约定

原创矢量图形沿用已批准界面 Demo 的 `#315CE7` 品牌蓝：圆角蓝色方块承载白色折角文档，文档上的两条蓝色短线表示内容；右下角白色放大镜表示查找。放大镜周围的蓝色分隔区保持镜圈与纸张边界清晰。浅蓝折角使用 `#C8D5FF`。图标不含文字、外部品牌、外部资源、阴影或 SVG 滤镜。

设计坐标系为 `256 × 256`：

- 蓝色底板位于 `(8, 8)`，宽高 `240`，圆角半径 `52`，四角以透明 alpha 融入桌面或窗口背景。
- 文档范围约为 `x=61…177, y=45…195`，折角由 `(136,45)` 延伸到 `(177,86)`。
- 文档线条位于 `y=110/137`，线宽 `12`；保留两条明显的长短线，避免依靠微小装饰表达内容。
- 镜圈中心 `(166,166)`、半径 `30`、白描边 `12`；蓝色分隔圆半径 `42`。
- 镜柄从 `(188,188)` 到 `(211,211)`，白色圆端线宽 `13`；背景分隔线宽 `28`。包括端点和描边的范围均位于底板内。

所有尺寸使用相同构图。16 px 时仍保留文档轮廓、折角、文档线和放大镜轮廓；没有为小尺寸另画不同标志。光栅化采用高分辨率抗锯齿后双三次下采样，保留半透明边缘。

## 资产

所有路径相对于仓库根目录。

| 文件 | 内容与用途 |
| --- | --- |
| [矢量 SVG](../src/LocalDocSearch.Desktop/Assets/app-icon.svg) | 原始矢量，256 viewBox，仅 rect/path/circle，无文字与外部依赖 |
| [Windows ICO](../src/LocalDocSearch.Desktop/Assets/app-icon.ico) | Windows 应用 / PE 与 Avalonia WindowIcon；9 帧，全部为独立 RGBA PNG 负载 |
| [256 PNG](../src/LocalDocSearch.Desktop/Assets/app-icon.png) | 256 × 256 RGBA，Avalonia 品牌标记 / Image 资源 |
| [512 PNG](../src/LocalDocSearch.Desktop/Assets/app-icon-512.png) | 512 × 512 RGBA，高分辨率展示 |
| [浅色/深色接触表](../src/LocalDocSearch.Desktop/Assets/app-icon-preview.png) | 1040 × 720，9 个尺寸均按原始像素大小展示 |
| [完整图标清单](../src/LocalDocSearch.Desktop/Assets/app-icon-manifest.json) | 色板、每帧尺寸、PNG 字节数、SHA-256、alpha 像素统计以及完整 ICO / SVG / PNG 哈希 |

ICO 帧顺序固定为 `16, 20, 24, 32, 40, 48, 64, 128, 256`。目录采用标准 ICONDIR / ICONDIRENTRY：保留值 `0`、类型 `1`、planes `1`、bit count `32`，PNG 数据从 `6 + 16 × 9 = 150` 字节开始。每个偏移由前一帧累计长度计算，256 px 的宽高目录字节均为 `0`。PNG 编码负载面向现代 Windows 图标解码器；没有附加 BMP XOR/AND mask。

## 再生成

环境：Windows、PowerShell 7，以及该 Windows .NET 运行时提供的 `System.Drawing.Common`。没有新 NuGet/npm 依赖、网络下载或项目编译步骤。

在仓库根目录运行：

```powershell
pwsh -NoProfile -File .\scripts\generate-app-icon.ps1
```

脚本中的 `$scene` 是唯一几何源。SVG 导出器与 System.Drawing 绘制器逐个消费相同的图形对象及路径命令；修改构图应编辑这份数据并重新运行脚本，避免单独修改生成 SVG 或位图。

脚本通过自身位置确定仓库，不依赖当前终端目录。输出写入固定的六个 Assets 文件，不编辑项目、应用入口、窗口或本文档。已有六个输出会覆盖更新；没有删除其他资产的行为。生产资产不含生成时间戳。同一 Windows / System.Drawing 环境可重复生成并对比 manifest 哈希；更换运行时或 GDI+ 实现可能改变边缘采样与 PNG 字节，接触表文字也取决于系统 Segoe UI。

## 自动校验与审阅

生成器在写入前及保存后进行校验：

1. 每个图形连同描边扩展范围均位于底板的 `8…248` 坐标范围内，涵盖镜柄。
2. SVG 作为 XML 解析，禁用 DTD / 外部解析器，并校验命名空间及保存后读回内容。
3. 所有 PNG 验证签名、IHDR 的宽高 / 8-bit RGBA、IDAT / IEND 完整性、无尾部冗余，以及每个 chunk 的标准 CRC32。
4. 解码 PNG 后统计透明、半透明、全不透明像素；每个图标帧必须同时具有三类像素，四角完全透明。
5. ICO 读回每个目录项，验证帧数、尺寸、保留位、planes、位深、长度、连续累计偏移和整文件总长度；逐帧核对负载 SHA-256 并重新执行 PNG 校验。
6. 用 Windows `System.Drawing.Icon` 原生解码读回完整 ICO。

生成器打印所有帧的尺寸、长度、SHA-256 和 alpha 统计，完整记录也保存在 manifest。上述验证覆盖结构与像素数据，不代替视觉评审。

本轮图片读取工具未提供图像输入，已完成结构、解码及像素数据校验，未把这些检查表述为视觉审图。可打开[接触表](../src/LocalDocSearch.Desktop/Assets/app-icon-preview.png)，重点核对 16 / 20 / 24 px 的文档线条与镜圈，以及两个背景下的边缘表现。

## 已接入正式应用

- [项目配置](../src/LocalDocSearch.Desktop/LocalDocSearch.Desktop.csproj)通过 `ApplicationIcon` 将[九帧 ICO](../src/LocalDocSearch.Desktop/Assets/app-icon.ico)写入 Windows EXE，同时将 ICO 和 256 PNG 打包为 Avalonia 资源。
- [应用主题](../src/LocalDocSearch.Desktop/App.axaml)通过全局 `Window.Icon` 使用同一 ICO；主窗口、高级搜索和独立预览沿用同一标识。
- [BrandMark 控件](../src/LocalDocSearch.Desktop/Controls/BrandMark.axaml)显示 256 PNG，按正方形保持比例，用于主窗口和高级搜索的品牌栏。PNG 自带蓝色底板与透明圆角。
- 32 / 40 / 48 逻辑像素的品牌图形可直接缩放 256 PNG；高分辨率介绍材料可使用[512 PNG](../src/LocalDocSearch.Desktop/Assets/app-icon-512.png)或[SVG](../src/LocalDocSearch.Desktop/Assets/app-icon.svg)。
- [PE 图标核验工具](../artifacts/history/tools/verify-demo-icon-pe.ps1)只读提取已构建 EXE 的图标组，逐帧比较尺寸、PNG 哈希并用 Windows 解码器重建检查；完整候选结果汇总于[本轮交付报告](demo-alignment-results.md)。
