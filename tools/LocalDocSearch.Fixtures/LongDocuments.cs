using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalDocSearch.Fixtures;

/// <summary>Small original artifacts with late matches and unambiguous structural locations.</summary>
public static class LongDocuments
{
    public const int PageCount = 200;
    public const int TextLineCount = 100_000;
    public const int CsvRecordCount = 20_000;
    public const string PdfName = "原创长文档-200页.pdf";
    public const string WordName = "原创长文档-200页.docx";
    public const string TextName = "原创长文本-100000行.txt";
    public const string CsvName = "原创长表格-20000记录.csv";
    public const string RepeatedTerm = "长文定位西瓜";
    public const string TailTerm = "长文末尾定位";

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Topics = ["河岸巡查", "苗圃整理", "雨量登记", "廊道照明", "库房通风", "水渠保养", "工具归架", "种子留样", "屋顶排水", "路标复查", "温室遮阳", "木桥养护"];
    private static readonly string[] Observations = ["清晨的薄雾散去后，沿线刻度清晰可辨。", "午后的光线落在记录板上，笔迹保持完整。", "阵雨结束后，边缘积水已按顺序排走。", "晚风经过廊下，悬挂的标牌保持平整。"];
    private static readonly string[] Actions = ["先核对入口编号，再沿顺时针方向逐项复查。", "把暂存器具分区归位，并登记交接时的数量。", "沿外侧检查连接处，确认后再整理中央通道。", "保留一份现场草图，便于下一班按相同路线复核。"];

    /// <remarks>Writes only the four named artifacts and extensionless README in the requested directory.</remarks>
    public static void CreateAll(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        PdfFixture.Create(Path.Combine(root, PdfName), Enumerable.Range(1, PageCount)
            .Select(page => (IReadOnlyList<string>)PageLines("PDF", page)).ToArray(), fontSize: 11);
        CreateWord(Path.Combine(root, WordName));
        CreateText(Path.Combine(root, TextName));
        CreateCsv(Path.Combine(root, CsvName));
        foreach (string name in new[] { PdfName, WordName, TextName, CsvName })
        {
            if (new FileInfo(Path.Combine(root, name)).Length > 20L * 1024 * 1024)
                throw new InvalidDataException($"Fixture exceeds 20 MiB: {name}");
        }
        WriteReadme(root);
    }

    private static string[] PageLines(string format, int page)
    {
        string topic = Topics[(page - 1) % Topics.Length];
        string observation = Observations[(page - 1) % Observations.Length];
        string action = Actions[(page - 1) / Topics.Length % Actions.Length];
        string context = page switch
        {
            150 => $"{RepeatedTerm}：雨后东棚复核完成。",
            200 => $"{RepeatedTerm}：终卷南库封存完成。",
            _ => $"复查结论：第{page:000}组记录与现场相符。"
        };
        return [
            $"第{page:000}页：{topic}观察手记",
            $"LD_{format}_PAGE_{page:000}",
            $"原创记录第{page:000}号，地点为第{(page - 1) % 20 + 1:00}号试验区。",
            observation,
            $"本轮查看{page % 7 + 3}处节点，逐一登记外观与使用状态。",
            action,
            $"样本架第{page % 9 + 1}层已清点，记录卡按编号放回。",
            "交接时先读原始备注，再核对新添的观察结果。",
            $"下一轮从第{page % 20 + 1:00}号试验区开始，复用本页路线。",
            context,
            page is 150 or 200 ? $"LD_{format}_P{page:000}_ANCHOR" : $"复核签记：本页序号{page:000}已经登记。",
            page == PageCount ? $"{TailTerm}：终卷已封存。LD_{format}_END" : "本页记录到此，后页继续下一处现场观察。"
        ];
    }

    private static void CreateWord(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new W.Styles(new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "宋体" },
                new W.FontSize { Val = "22" })),
            new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                new W.SpacingBetweenLines { After = "100", Line = "340", LineRule = W.LineSpacingRuleValues.Auto }))));
        var body = new W.Body();
        for (int page = 1; page <= PageCount; page++)
        {
            var lines = PageLines("DOCX", page);
            for (int index = 0; index < lines.Length; index++)
            {
                var paragraph = new W.Paragraph();
                if (index == 0)
                {
                    paragraph.Append(new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "0", After = "180" }));
                    if (page > 1) paragraph.Append(new W.Run(new W.Break { Type = W.BreakValues.Page }));
                    paragraph.Append(new W.Run(new W.RunProperties(new W.Bold(), new W.FontSize { Val = "28" }),
                        new W.Text(lines[index])));
                }
                else paragraph.Append(new W.Run(new W.Text(lines[index])));
                body.Append(paragraph);
            }
        }
        body.Append(new W.SectionProperties(
            new W.PageSize { Width = 11906U, Height = 16838U },
            new W.PageMargin { Top = 1134, Right = 1134U, Bottom = 1134, Left = 1134U, Header = 567U, Footer = 567U, Gutter = 0U }));
        main.Document = new W.Document(body);
        main.Document.Save();
        styles.Styles.Save();
        var errors = new OpenXmlValidator().Validate(document).Take(5).ToArray();
        if (errors.Length > 0)
            throw new InvalidDataException("Long DOCX validation failed: " + string.Join("; ", errors.Select(error => error.Description)));
        int breaks = body.Descendants<W.Break>().Count(item => item.Type?.Value == W.BreakValues.Page);
        if (breaks != PageCount - 1) throw new InvalidDataException("Long DOCX must have 199 manual page breaks.");
    }

    private static void CreateText(string path)
    {
        using var writer = new StreamWriter(path, false, Utf8) { NewLine = "\n" };
        for (int line = 1; line <= TextLineCount; line++)
        {
            writer.WriteLine(line switch
            {
                75_000 => $"巡检075000：{RepeatedTerm}：雨后东棚复核完成。LD_TXT_L075000_ANCHOR",
                TextLineCount => $"巡检100000：{RepeatedTerm}：终卷南库封存完成。{TailTerm}。LD_TXT_L100000_ANCHOR LD_TXT_END",
                _ => FormattableString.Invariant($"巡检{line:000000}：第{(line - 1) % 20 + 1:00}区登记完毕。")
            });
        }
    }

    private static void CreateCsv(string path)
    {
        using var writer = new StreamWriter(path, false, Utf8) { NewLine = "\n" };
        writer.WriteLine("编号,区域,原创备注");
        for (int record = 1; record <= CsvRecordCount; record++)
        {
            string note = record switch
            {
                15_000 => $"{RepeatedTerm}：雨后东棚复核完成。LD_CSV_R15000_ANCHOR",
                CsvRecordCount => $"{RepeatedTerm}：终卷南库封存完成。{TailTerm}。LD_CSV_R20000_ANCHOR LD_CSV_END",
                _ => $"原创巡检记录{record:00000}，已归档"
            };
            writer.WriteLine(FormattableString.Invariant($"{record},{(record - 1) % 20 + 1:00},\"{note}\""));
        }
    }

    private static void WriteReadme(string root)
    {
        string sizes = string.Join("\n", new[] { PdfName, WordName, TextName, CsvName }.Select(name =>
            $"- {name}：{new FileInfo(Path.Combine(root, name)).Length.ToString(CultureInfo.InvariantCulture)} bytes"));
        string readme = $$"""
            原创超长文档测试样本
            ==================
            本目录是可直接在资源管理器找到并用于搜索的原创样本；不依赖网络下载、Office进程或UI。
            README 特意没有扩展名，默认格式筛选不会搜索说明本身。
            内容为虚构的试验区巡检手记，由仓库内生成器编写；各页有独立编号、正文和稳定ASCII标记。

            文件与规模
            ----------
            - {{PdfName}}：真实200页文字PDF，A4，12行/页；STSong-Light标准中文字体与显式ToUnicode映射。
              字体沿用现有三页样本方式，未复制系统字体；显示端需要标准中文字体支持，文字提取不依赖OCR。
            - {{WordName}}：200个内容页，2400个正文段落，199个明确手动分页符，A4。
              每页12段原创正文，页150与页200有不同上下文，终卷最后一段有末尾定位词。
              Word手动分页200页不保证所有字体、打印机和打印环境下的排版总页完全一样。
              本应用的Word位置是正文段落结构提示，不应当作渲染页码。
            - {{TextName}}：UTF-8无BOM，100000物理行，LF换行，末行保留换行。
            - {{CsvName}}：UTF-8无BOM，20000条数据记录+1行表头=20001物理行；3列，备注为第3字段。
              所有数据记录均为单物理行；备注列以CSV双引号包围，普通正文保留中文逗号。
            {{sizes}}
            每个样本小于20MiB。目录只有上述四个可搜索文件和本说明；重建仅替换这五个同名文件。

            关键词与预期命中
            ----------------
            选择本目录，勾选.pdf/.docx/.txt/.csv，使用默认严格Unicode模式。
            - 长文定位西瓜：每文件2次，共8次；西瓜得到同样8次。
              前一次上下文“雨后东棚复核完成”，后一次上下文“终卷南库封存完成”。
            - 长文末尾定位：每文件1次，共4次；均在各文件结尾。
            - 苹果：0次，供验证无结果。
            - PDF：前后命中真实来源页150/200；ASCII唯一标记LD_PDF_P150_ANCHOR与LD_PDF_P200_ANCHOR。
            - DOCX：前后关键词分别为正文段落1798/2398；ASCII标记分别为1799/2399段：
              LD_DOCX_P150_ANCHOR、LD_DOCX_P200_ANCHOR。末尾词和LD_DOCX_END同在最后2400段，不跨segment。
              LD_DOCX_PAGE_150与LD_DOCX_PAGE_200在各内容页标题后的独立段，便于原生查找。
            - TXT：物理行75000/100000；LD_TXT_L075000_ANCHOR与LD_TXT_L100000_ANCHOR。
            - CSV：数据记录15000/20000（含表头时提取来源记录15001/20001）、第3字段、物理行15001/20001；
              LD_CSV_R15000_ANCHOR与LD_CSV_R20000_ANCHOR。
            - 末尾ASCII标记：LD_PDF_END、LD_DOCX_END、LD_TXT_END、LD_CSV_END，各自唯一。
            搜索共享词后选择前/后一次命中，可检验重复词定位；查唯一ASCII标记可稳定核对指定内容页。
            搜索上下文短语可在每文件中缩小到1处；搜索单个ASCII标记可得到1个指定文件的1处命中。

            重建（在仓库根目录运行，只构建Fixtures项目）
            ------------------------------------------------
            $env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/dotnet-home'
            $env:NUGET_PACKAGES = Join-Path $PWD '.tools/nuget/packages'
            ./.tools/dotnet/dotnet.exe run --project tools/LocalDocSearch.Fixtures -c Release --no-restore -- --long tests/fixtures/long-documents
            默认模式与--encoding仍按原语义生成小样本，不会被--long隐式调用；本目录不覆写mixed或encoding。

            提取与搜索核验（独立harness；复用已构建的Release提取器，不启动Office）
            -------------------------------------------------------------------------
            ./.tools/dotnet/dotnet.exe run --project tools/LocalDocSearch.Fixtures/Verification -c Release -- tests/fixtures/long-documents
            前提：仓库现有Core/Extractors的Release输出已存在。harness只引用这些现成DLL，
            不重建父项目，不添加NuGet包，不修改全局solution或包配置。
            它会从磁盘实际回读PDF200页与150/200来源页、验证DOCX的199个分页符和2400段，
            使用现有PDF/Word/TXT/CSV提取器和TextSearchEngine核验8次重复词、4次末尾词、ASCII标记及0次苹果。
            结果打印JSON证据；退出码0表示全部断言通过。Word末尾词必须完整位于最后一个segment。
            """;
        File.WriteAllText(Path.Combine(root, "README"), readme + "\n", Utf8);
    }
}
