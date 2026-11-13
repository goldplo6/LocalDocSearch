# Contributing

项目仓库为 <https://github.com/goldplo6/LocalDocSearch>，问题报告入口为 <https://github.com/goldplo6/LocalDocSearch/issues>。当前交付仍以本地候选和实验验证为主；仓库内的候选构建工作流需手动触发，不会因首次源码上传自动生成正式发布。请提交最小复现和修改说明。

## 开发与验证

使用 PowerShell 7 和项目固定的 .NET SDK；具体版本见候选包的 `build-record.json` 或源码的 `global.json`。源码仓库中的 `scripts/bootstrap-sdk.ps1` 安装项目本地 SDK，`scripts/dev.ps1 -Action Test -Locked` 执行锁定依赖测试。完整候选构建命令与隔离输出见 [发布指南](docs/release-guide.md)。下载 SDK 和首次 NuGet 还原需要联网；新增依赖要同时检查精确版本、锁文件、许可证全文、原生 NOTICE 与分发影响。

请先阅读现有模块与测试，保持搜索只读、来源位置可核验、取消/预算反馈明确、偏好不保存关键词或正文。代码变更应附能够证明行为的测试与实际执行结果。不要把开发机器路径、私人文档、设置、凭据、SDK 缓存、bin/obj 或生成日志当作提交内容。原创夹具优先；提交外部文档前确认其分发权利并移除个人数据。

## 许可证和署名

项目自有代码采用 [MIT](LICENSE)，署名为 `2026 LocalDocSearch contributors`。贡献者保留自己的版权；维护者只合并权利来源明确、与 MIT 分发方式兼容的贡献。第三方代码、样本与字体应保留原始署名和适用许可，登记于 [第三方声明](THIRD-PARTY-NOTICES.md) 和机器可读依赖清单。不要把自己的姓名替换成运行时用户、工具账号或生成器身份。

## 问题报告

描述候选版本、系统架构、预期/实际结果及最小原创样本。优先使用应用主动复制的聚合诊断摘要；提交前自行检查截图、路径和文档片段。涉及敏感问题时遵循 [安全说明](docs/security.md)，不要公开私人文档或凭据。
