# 本地产物导航

## 常用入口

| 用途 | 位置 |
| --- | --- |
| 最新 rc.13 程序 | [DocSearch.exe](releases-control-colors/LocalDocSearch-1.0.0-rc.13-win-x64/LocalDocSearch-1.0.0-rc.13-win-x64/DocSearch.exe) |
| 最新 rc.13 完整压缩包 | [ZIP](releases-control-colors/LocalDocSearch-1.0.0-rc.13-win-x64/LocalDocSearch-1.0.0-rc.13-win-x64.zip) |
| rc.12 程序／逐格式验收基线 | [DocSearch.exe](releases-format-optimization/LocalDocSearch-1.0.0-rc.12-win-x64/LocalDocSearch-1.0.0-rc.12-win-x64/DocSearch.exe) |
| rc.12 完整压缩包／逐格式验收基线 | [ZIP](releases-format-optimization/LocalDocSearch-1.0.0-rc.12-win-x64/LocalDocSearch-1.0.0-rc.12-win-x64.zip) |
| rc.12 验收 | [逐格式优化报告](../docs/format-optimization-results.md) · [独立审计](releases-format-optimization/LocalDocSearch-1.0.0-rc.12-win-x64/final-audit.json) |
| rc.11 程序／复测基线 | [DocSearch.exe](releases-ui-csv/LocalDocSearch-1.0.0-rc.11-win-x64/LocalDocSearch-1.0.0-rc.11-win-x64/DocSearch.exe) |
| rc.11 完整压缩包／复测基线 | [ZIP](releases-ui-csv/LocalDocSearch-1.0.0-rc.11-win-x64/LocalDocSearch-1.0.0-rc.11-win-x64.zip) |
| rc.11 验收 | [优化报告](../docs/ui-csv-optimization-results.md) |
| rc.10 程序／复测基线 | [DocSearch.exe](releases-fixed-overhead/LocalDocSearch-1.0.0-rc.10-win-x64/LocalDocSearch-1.0.0-rc.10-win-x64/DocSearch.exe) |
| rc.10 完整压缩包／复测基线 | [ZIP](releases-fixed-overhead/LocalDocSearch-1.0.0-rc.10-win-x64/LocalDocSearch-1.0.0-rc.10-win-x64.zip) |
| rc.10 验收 | [优化报告](../docs/fixed-overhead-results.md) · [独立审计](releases-fixed-overhead/LocalDocSearch-1.0.0-rc.10-win-x64/final-audit.json) |
| rc.9 完整复测基线 | [发布目录](releases-literal-prefilter/LocalDocSearch-1.0.0-rc.9-win-x64/) |
| 原运行版本 | [保留目录](root-relocated/LocalDocSearch/) |
| 本次目录归类 | [迁移计划与原路径映射](organization/plan.json) |
| 历史材料 | [按用途归档](history/) |

项目根目录的[启动最新版.cmd](../启动最新版.cmd)启动上述 rc.13。rc.12、rc.11、rc.10、rc.9 和原运行目录作为复测／回滚基线继续保留。发布目录中的运行时、DLL、许可证与清单整体保留。

## 历史材料分类

- `history/builds`：历史源码快照、构建和测试记录。
- `history/releases`：旧候选的剩余轻量验收记录。
- `history/performance`：历史性能和并发实验。
- `history/search`：搜索、分组、暂停与滚动交互验证。
- `history/ui`：界面、图标、Demo 与预览实验。
- `history/navigation`：原程序定位及历史诊断。
- `history/maintenance`：旧清理与目录迁移记录。
- `history/samples`：历史实验样本。
- `history/tools`：历史辅助脚本与同伴源码；其中六个正式并发实验脚本移至 `scripts/experiments`。
- `history/records`、`history/settings`、`history/logs`、`history/screenshots`：原先散落在根部的报告、测试设置、日志和截图。

当前发布包、原始性能矩阵、原运行目录及复测驱动／语料仍在原位置，避免中断当前复测依赖。完整保护集见迁移计划中的 `protected` 列表。

历史 JSON、原始日志、截图和测量数据按原字节归档；其中的绝对路径描述当时运行环境。历史路径可通过迁移计划中的 `moves`（目录级）或 `files`（逐文件）找到新位置。当前文档直链和工具路径已随本次归类维护；这不代表已删除旧候选的每个历史操作都能直接重演。49 份需要修改路径的脚本另存原始字节于 `history/tools/originals`，原哈希与位置记在 `organization/original-script-evidence.json`，供历史审计溯源。

本目录的大型产物及归类记录仍只保留本地；源码仓库纳入本导航，常用开发与发布命令见[项目说明](../README.md)。
