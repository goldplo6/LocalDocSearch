# 第五阶段：本地候选交付指南

当前目标是 **Windows 11 x64 本地候选 ZIP**，版本示例为 `1.0.0-rc.1`。这不表示 v1.0.0 已公开发布，不表示签名、干净机器或真实 360 认证已完成。版本、运行时、测试计数、锁文件哈希及已生成文件应以该包 `build-record.json`、依赖清单和外部 SHA256 为准。

## 便携包用户

先核对 ZIP SHA256，再完整解压运行 `DocSearch.exe`（rc.7 起；旧候选的启动名为 `LocalDocSearch.Desktop.exe`）。保留同目录运行时、DLL、docs 和许可证。包为自包含应用，无需另外安装 .NET；搜索与只读文字预览不需要 Office。搜索使用普通用户权限。遇到未知发布者或杀毒提示时保留提示和哈希交由维护者调查，保持原有防护设置。隐私和设置路径见 [隐私说明](privacy.md)，详细处理见 [安全说明](security.md)。

发布包内 README 是独立便携说明，链接只指向包内实际携带的文档；它不复制源码根 README 中的源码、报告或开发产物链接。样本为可选；启用后提供原创八格式小样本及四份长文档。样本不是外部商业/私人文档。

## 源码维护者执行

以下命令在源码根目录、PowerShell 7 下执行，便携 ZIP 不附带构建 SDK、源码或脚本。先确保源码集成稳定，并与其他构建者协调 CPU；脚本串行执行 MSBuild，SDK/包缓存可能被其他开发操作共享。

```powershell
./scripts/bootstrap-sdk.ps1
./scripts/dev.ps1 -Action Restore -Locked
./scripts/release-script-tests.ps1
./scripts/release.ps1 -Version 1.0.0-rc.1 -IncludeSamples
./scripts/release-verify.ps1 -CandidateDirectory ./artifacts/releases/LocalDocSearch-1.0.0-rc.1-win-x64
```

SDK 要与 `global.json` 的精确版本一致，本阶段为 `10.0.403`。脚本使用项目 `.tools/dotnet/dotnet.exe`，将 `DOTNET_CLI_HOME` 和 `NUGET_PACKAGES` 指向项目 `.tools` 下的隔离目录，关闭 .NET CLI 遥测。首次 SDK 下载和包还原需要联网；许可正文已随仓库存放，候选生成不再次下载许可证。

默认输出位于 `artifacts/releases/LocalDocSearch-<版本>-win-x64/`：

- `LocalDocSearch-<版本>-win-x64.zip`：可分享的自包含候选。
- `SHA256SUMS.txt`：ZIP 的 SHA256，与 ZIP 一同交付。
- 同名展开目录：应用、独立 README、隐私/安全/发布文档、MIT 和第三方声明、依赖证据、`build-record.json`、`file-manifest.json` 及可选样本。
- `build-input/`、`source-inputs.json`、`logs/`、`test-results/`、`validation.json`：本地构建验证证据，**不在用户 ZIP 内**。日志和 TRX 可能带开发机器路径，不作为公开诊断附件直接分享。

已有同名候选目录会立即停止；脚本不覆盖或自动清理。失败的目录保留现场，并且没有成功验证声明。修复问题后使用新的候选版本或 `-OutputRoot` 指定新的隔离根。默认没有跳过测试、忽略构建失败或绕过许可证阻塞的开关。`release-script-tests.ps1` 只做解析、真实许可证/ZIP/白名单快照及失败分支检查，不替代完整产品回归。

已有完整候选测试证据时，可附加真实 TRX 的汇总回归，检查 ordered dictionary 数值累加以及单项目/多项目 JSON 数组形状：

```powershell
./scripts/release-script-tests.ps1 -TrxDirectory ./artifacts/releases/LocalDocSearch-1.0.0-rc.1-win-x64/test-results
```

该命令只读取已有 TRX，不复跑产品测试；失败现场继续保留。脚本修复后的完整发布验证应使用新的 `-OutputRoot` 再执行全流程。

## 锁定、隔离及复现范围

1. 只复制明列的源文件、项目/中央配置、锁文件和原创夹具到 `build-input/`，按字节 SHA256 核对；排除 `.tools`、bin/obj、个人设置、用户文档和凭据。源码的锁文件与快照初始副本哈希一致。
2. 在快照内用现有源锁 `restore --locked-mode`，再构建整个解决方案及逐测试项目运行真实测试，保存 TRX 和汇总计数。不会占用用户仍在运行的旧 `src/.../bin` 文件，也不关闭用户应用。
3. 源锁尚未冻结 win-x64 图时，只在快照内为 Desktop 执行 RID resolve，固定 SDK 的 `LatestRuntimeFrameworkVersion`，并确认原有 30 个 Desktop NuGet 包版本没有增删变化。记录候选 RID 锁哈希后以同样参数再次 `--locked-mode` 还原；不回写源锁。这是**候选专用生成再锁定**，不是“仓库早已提交完整冻结 RID 图”。
4. 在同一快照 `publish --no-restore -r win-x64 --self-contained true`，不裁剪、不使用 NativeAOT、不打单文件、不 UPX。`PathMap` 把快照绝对根映射成 `/_/src`，避免发布 PDB 写入个人源码根；构建记录只记相对路径。
5. 校验原始 nupkg 的 NuGet canonical content hash（处理签名 ZIP 差异）与 assets 一致，另记原始 nupkg SHA256。保留实际 .NET 运行时包的许可证和第三方 NOTICE，并校验发布 coreclr.dll 与所记运行时包一致。
6. 生成逐文件 SHA256 和 ZIP，按 ordinal 顺序固定条目、统一 ZIP 时间为 `2026-01-01T00:00:00Z`；再独立校验目录文件集、PE x64 架构、自包含配置、RID、证据哈希、ZIP 各条目及外部摘要。

上述工作固定并记录输入，提供稳定打包规则。尚未证明两台干净环境能生成逐字节相同输出；SDK 工具、压缩实现、包签名和整个构建过程的其他差异仍需独立复现对照。不得把“依赖锁已复验”表述成“已经证明所有 clean build 字节一致”。

## 许可证选择与证据

按本阶段默认决策，项目自有代码采用 MIT，版权为 `2026 LocalDocSearch contributors`，便于保持简单、宽松的项目分发方式；这不是对第三方作品的重新许可。贡献者保留其作品版权。详细原文见 [MIT](../LICENSE) 和 [第三方声明](../THIRD-PARTY-NOTICES.md)。

`license-inventory.ps1` 读取实际 Desktop `project.assets.json`、包目录 nuspec 和原始 nupkg；复制随包全文及 NOTICE。缺失全文时只接受 nuspec 指明的 exact repository commit 与校验过 SHA256 的上游材料。Avalonia、Open XML、PdfPig、MicroCom、Tmds、System.IO.Packaging 的补充证据随源码保留；Skia/HarfBuzz 原生 notices 不简写、不本地化。清单覆盖完整 Desktop 图，含保守保留的非 Windows/构建依赖；测试专用包不随产品分发。

未知表达式、未审查的 file-license 包、缺全文、包/证据哈希冲突、运行时声明缺失都会阻止候选生成，具体阻塞保存在生成清单的 `unresolved`。当前已接受元数据包括 MIT、Apache-2.0，以及特定 ANGLE 原生包自带的 BSD 文本；ANGLE 不会被擅自改标 MIT。依赖元数据与原始声明汇集不等于独立法律意见或全部原生源码溯源审计。

## 手动 CI 与后续人工验收

源码中的 `.github/workflows/release-candidate.yml` 仅提供 `workflow_dispatch` Windows 构建、锁验证、测试、打包及候选 artifact 上传；不创建 GitHub Release、不推送标签、不发布包。项目尚无远端仓库配置，因此 workflow 是待接入的自动化定义，不是已经成功执行的云端构建证据。

交付前记录至少：ZIP 哈希及可打开性、全新 Windows 11 x64 无 SDK/.NET 条件下启动、中文目录/IME、键盘与滚轮、多 DPI、设置隔离、八格式/长样本计数、取消与错误反馈，以及目标 Office/PDF 默认应用行为。真实 360 检查应记录产品/病毒库版本、文件哈希、提示及结果；未运行就标待测，不由静态扫描或自动化测试代替。签名、证书声誉、完整第三方漏洞检查和公开发布通道仍需单独完成并写入版本验收记录。
