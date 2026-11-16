# LocalDocSearch

Windows 本地文档全文搜索工具。离线、只读，支持 TXT、MD、LOG、CSV、DOCX、XLSX、PPTX 和文字型 PDF。

## 使用

1. 解压 Windows 自包含运行包，打开 `DocSearch.exe`；运行包自带 .NET 运行时。源码仓库不包含运行包。
2. 选择文件夹和文件格式，输入关键词并添加，用 AND／OR 组合条件后开始搜索。
3. 复杂条件进入“高级搜索”：逻辑搜索支持可视化分组和 `西瓜 AND (苹果 OR 荔枝)`；正则匹配可输入 `PRJ-\d{4}`。
4. 展开结果查看命中位置，使用只读文字预览核对内容，也可打开原文件或所在目录。搜索过程中可暂停、继续或停止。

AND／OR 判断同一文件内是否满足条件。每轮重新读取文件，不建立全文索引或跨次全文缓存；扫描图片和扫描型 PDF 需要先做 OCR。默认按 Unicode 读取文本，可在设置中选择 GB18030 兼容模式。

## 从源码运行

安装 PowerShell 7，在仓库根目录执行：

```powershell
./scripts/bootstrap-sdk.ps1
./scripts/dev.ps1 -Action Run -Locked
```

已有匹配 `global.json` 的 .NET SDK 时，也可直接运行：

```powershell
dotnet restore LocalDocSearch.slnx --locked-mode
dotnet run --project src/LocalDocSearch.Desktop -c Release --no-restore
```

AI 助手使用步骤见 [DocSearch skill](skills/docsearch/SKILL.md)。项目使用 [MIT 许可证](LICENSE)，依赖声明见 [第三方许可证](THIRD-PARTY-NOTICES.md)。
