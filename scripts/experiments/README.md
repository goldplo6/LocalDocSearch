# 受限并发实验脚本

这些六个受版本控制的脚本原先散落在本地产物目录，现集中于此；项目根目录仍是运行命令的工作目录。

| 用途 | 脚本 |
| --- | --- |
| 构建实验版本 | [build-concurrency-experiment.ps1](build-concurrency-experiment.ps1) |
| 引擎性能与结果等价比较 | [measure-concurrency-experiment.ps1](measure-concurrency-experiment.ps1) |
| 桌面实验测量 | [measure-concurrency-desktop.ps1](measure-concurrency-desktop.ps1) |
| 原生窗口端到端测量 | [measure-concurrency-ui.ps1](measure-concurrency-ui.ps1) |
| 暂停／继续／停止原生验证 | [verify-concurrency-controls-uia.ps1](verify-concurrency-controls-uia.ps1) |
| 实验验收核对 | [verify-concurrency-experiment.ps1](verify-concurrency-experiment.ps1) |

[实验报告](../../docs/bounded-concurrency-results.md)与[基准工具说明](../../tools/LocalDocSearch.Benchmarks/README.md)记录调用方法及测量边界。当前 rc.9／rc.10 的专用测量和验包入口仍在上一级脚本目录。

历史辅助脚本归入[历史工具](../../artifacts/history/tools/)。历史 JSON 保留原字节及当时路径，迁移位置见[归类映射](../../artifacts/organization/plan.json)；旧报告与旧脚本哈希用于历史溯源，整理后的脚本不等同当时的原始字节。
