using System.Text;
using Avalonia.Headless.XUnit;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.ViewModels;
using LocalDocSearch.Extractors;
using LocalDocSearch.Fixtures;
using LocalDocSearch.Navigation;
using LocalDocSearch.Settings;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class SettingsAndNavigationTests
{
    [AvaloniaFact]
    public void SettingsWindowLoadsAndDraftDoesNotChangeOriginalUntilApplied()
    {
        var original = new AppSettings();
        var draft = new SettingsViewModel(original);
        var window = new SettingsWindow { DataContext = draft };
        window.Show();
        draft.EncodingIndex = 1; draft.ExcelHiddenSheets = true; draft.WordTables = false;
        draft.SortMode = 3; draft.ContextCharacters = 120;
        var changed = draft.Build();
        Assert.Equal(TextEncodingMode.StrictUnicode, original.EncodingMode);
        Assert.False(original.Scope.ExcelHiddenSheets);
        Assert.Equal(TextEncodingMode.Utf8OrGb18030, changed.EncodingMode);
        Assert.True(changed.Scope.ExcelHiddenSheets);
        Assert.False(changed.Scope.WordTables);
        Assert.Equal(3, changed.SortMode); Assert.Equal(120, changed.ContextCharacters);
        window.Close();
    }

    [AvaloniaFact]
    public void PreferencesPersistWithoutQueriesAndCorruptSettingsSurviveAutomaticSave()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonSettingsStore(path);
        var vm = new MainWindowViewModel(store) { RootFolder = temp.Path, PendingTerm = "PRIVATE_QUERY_DO_NOT_STORE" };
        vm.ApplySettings(vm.CaptureSettings() with { ContextCharacters = 20, EncodingMode = TextEncodingMode.Utf8OrGb18030, Scope = new DocumentScopeOptions(ExcelHiddenSheets: true) }, save: true);
        var restored = new MainWindowViewModel(new JsonSettingsStore(path));
        Assert.Equal(temp.Path, restored.RootFolder); Assert.Equal(20, restored.ContextCharacters);
        Assert.Equal(1, restored.EncodingIndex); Assert.True(restored.CaptureSettings().Scope.ExcelHiddenSheets);
        Assert.DoesNotContain("PRIVATE_QUERY_DO_NOT_STORE", File.ReadAllText(path));
        const string damaged = "{broken configuration";
        File.WriteAllText(path, damaged);
        var invalid = new MainWindowViewModel(new JsonSettingsStore(path));
        Assert.NotEmpty(invalid.SettingsWarning);
        invalid.SaveSettings();
        Assert.Equal(damaged, File.ReadAllText(path));
        invalid.ApplySettings(invalid.CaptureSettings(), save: true);
        Assert.Null(store.Load().Warning); Assert.Empty(invalid.SettingsWarning);
    }

    [AvaloniaFact]
    public async Task CorrectlyConvertedCsvStillMatchesAndLegacyCompatibilityIsOptional()
    {
        using var temp = new TempDirectory();
        EncodingSamples.CreateAll(temp.Path);
        var before = Directory.GetFiles(temp.Path).ToDictionary(p => p, p => File.ReadAllBytes(p));
        var vm = new MainWindowViewModel { RootFolder = temp.Path, PendingTerm = "西瓜" };
        vm.AddTermCommand.Execute(null); vm.PendingTerm = "苹果";
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Results.Count); Assert.Equal(6, vm.Results.Sum(f => f.TotalOccurrences));
        Assert.Contains(vm.Results, f => f.FullPath.EndsWith("已转为UTF8.csv"));
        vm.ApplySettings(vm.CaptureSettings() with { EncodingMode = TextEncodingMode.Utf8OrGb18030 });
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Results.Count); Assert.Equal(8, vm.Results.Sum(f => f.TotalOccurrences));
        Assert.Contains(vm.Issues, i => i.Severity == SearchIssueSeverity.Warning && i.Message.Contains("GB18030"));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [AvaloniaFact]
    public async Task ChangedFormatScopeIsUsedByNextSearch()
    {
        using var temp = new TempDirectory();
        SampleDocuments.CreateAll(temp.Path);
        var vm = new MainWindowViewModel { RootFolder = temp.Path, PendingTerm = "隐藏专用词" };
        await vm.StartSearchCommand.ExecuteAsync(null); Assert.Empty(vm.Results);
        vm.ApplySettings(vm.CaptureSettings() with { Scope = new DocumentScopeOptions(ExcelHiddenSheets: true) });
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.EndsWith(".xlsx", Assert.Single(vm.Results).FullPath);
        vm.ApplySettings(vm.CaptureSettings() with { Scope = new DocumentScopeOptions() });
        await vm.StartSearchCommand.ExecuteAsync(null); Assert.Empty(vm.Results);
    }

    [AvaloniaFact]
    public async Task SortingAndContextUseSearchSettings()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "ABCDEFGHIJ西瓜KLMNOPQRST");
        File.WriteAllText(Path.Combine(temp.Path, "z.txt"), "西瓜 西瓜 西瓜");
        var vm = new MainWindowViewModel { RootFolder = temp.Path, PendingTerm = "西瓜" };
        vm.ApplySettings(vm.CaptureSettings() with { ContextCharacters = 3, SortMode = 2 });
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.EndsWith("z.txt", vm.Results[0].FullPath);
        var match = Assert.Single(vm.Results.Single(f => f.FullPath.EndsWith("a.txt")).VisibleMatches);
        Assert.Equal("HIJ", match.Before); Assert.Equal("KLM", match.After);
        vm.ApplySettings(vm.CaptureSettings() with { SortMode = 1 });
        Assert.EndsWith("a.txt", vm.Results[0].FullPath);
    }

    [AvaloniaFact]
    public async Task MatchCommandPassesTheChosenMatchAndReportsActualNavigationLevel()
    {
        var fake = new FakeNavigator();
        var vm = new MainWindowViewModel(navigator: fake);
        var first = new MatchItem("西瓜", "", "西瓜", "", new SourceLocator(SourceKind.ExcelCell, Sheet: "Sheet2", Address: "B15"));
        var second = first with { Location = new SourceLocator(SourceKind.ExcelCell, Sheet: "Sheet2", Address: "D20") };
        var file = new FileResultViewModel(new FileResult("chosen.xlsx", ".xlsx", DateTime.UtcNow, [first, second]));
        await vm.LocateMatchCommand.ExecuteAsync(file.VisibleMatches[1]);
        Assert.Equal("D20", fake.Request!.Match!.Location.Address);
        Assert.False(fake.Request.AllowOfficeAutomation); Assert.Contains("OpenedOnly", vm.StatusText);
        vm.AllowOfficeAutomation = true; fake.Level = LocateLevel.Exact;
        await vm.LocateFileCommand.ExecuteAsync(file);
        Assert.Equal("B15", fake.Request!.Match!.Location.Address);
        Assert.True(fake.Request.AllowOfficeAutomation); Assert.Contains("Exact", vm.StatusText);
        Assert.False(vm.IsLocating);
    }

    private sealed class FakeNavigator : IFileNavigator
    {
        public LocateRequest? Request { get; private set; }
        public LocateLevel Level { get; set; } = LocateLevel.OpenedOnly;
        public Task<LocateResult> LocateAsync(LocateRequest request, CancellationToken cancellationToken = default)
        {
            Request = request; return Task.FromResult(new LocateResult(Level, "fixture observation"));
        }
    }
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LocalDocSearch-p3-ui-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
