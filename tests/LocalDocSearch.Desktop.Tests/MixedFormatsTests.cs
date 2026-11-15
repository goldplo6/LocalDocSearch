using Avalonia.Headless.XUnit;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using LocalDocSearch.Fixtures;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class MixedFormatsTests
{
    [AvaloniaFact]
    public async Task DesktopDefaultsToEightFormatsAndShowsRealSourceLocations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalDocSearch-mixed-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            SampleDocuments.CreateAll(directory);
            var vm = new MainWindowViewModel { RootFolder = directory, PendingTerm = "西瓜" };
            vm.AddTermCommand.Execute(null);
            vm.PendingTerm = "苹果";
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.False(vm.IsBusy);
            Assert.Equal(8, vm.Results.Count);
            Assert.Equal(16, vm.Results.Sum(f => f.TotalOccurrences));
            foreach (var file in vm.Results) file.IsExpanded = true;
            var excel = Assert.Single(vm.Results, f => f.FullPath.EndsWith(".xlsx"));
            Assert.Contains(excel.VisibleMatches, m => m.Position.Contains("Sheet2") && m.Position.Contains("B15"));
            var slide = Assert.Single(vm.Results, f => f.FullPath.EndsWith(".pptx"));
            Assert.Contains(slide.VisibleMatches, m => m.Text == "西瓜" && m.Position.Contains("幻灯片 8"));
            var pdf = Assert.Single(vm.Results, f => f.FullPath.EndsWith(".pdf"));
            Assert.Contains(pdf.VisibleMatches, m => m.Text == "西瓜" && m.Position.Contains("PDF 页码 3"));
            Assert.Contains(vm.Results.SelectMany(f => f.VisibleMatches), m => m.Position.Contains("记录 2"));
            vm.UseOr = true;
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.Equal(8, vm.Results.Count);
            Assert.Equal(16, vm.Results.Sum(f => f.TotalOccurrences));
            vm.IncludeTxt = vm.IncludeMd = vm.IncludeLog = vm.IncludeCsv = vm.IncludeDocx = vm.IncludePptx = vm.IncludePdf = false;
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.EndsWith(".xlsx", Assert.Single(vm.Results).FullPath);
            Assert.Equal(2, vm.Results[0].TotalOccurrences);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SourceFormatterNeverPresentsWordParagraphsAsPages()
    {
        var word = SourcePositionFormatter.Format(new SourceLocator(SourceKind.WordParagraph, Paragraph: 13));
        Assert.Contains("段落 13", word);
        Assert.DoesNotContain("页", word);
        Assert.Contains("记录 2", SourcePositionFormatter.Format(new SourceLocator(SourceKind.CsvField, Line: 3, Record: 2, Field: 4)));
        Assert.Contains("行 2", SourcePositionFormatter.Format(new SourceLocation(2, 4)));
        Assert.Contains("公式缓存", SourcePositionFormatter.Format(new SourceLocator(SourceKind.ExcelCell, Sheet: "Sheet2", Address: "B15", ValueKind: "公式缓存")));
    }
}
