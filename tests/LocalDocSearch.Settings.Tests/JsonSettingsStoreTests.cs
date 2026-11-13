using System.Text.Json;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using Xunit;

namespace LocalDocSearch.Settings.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void MissingFileReturnsFreshDefaultsWithoutCreatingDirectories()
    {
        using var directory = new SettingsDirectory();
        var path = Path.Combine(directory.Root, "not-created", "settings.json");
        var store = new JsonSettingsStore(path);
        var first = store.Load();
        var second = store.Load();
        Assert.Null(first.Warning);
        AssertDefaults(first.Settings);
        Assert.NotSame(first.Settings.Extensions, second.Settings.Extensions);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal(path, store.FilePath);
    }

    [Fact]
    public void DefaultLocationIsLocalApplicationDataButConstructionDoesNotReadOrWriteIt()
    {
        var store = new JsonSettingsStore();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalDocSearch", "settings.json"), store.FilePath);
    }

    [Fact]
    public void RoundTripPersistsAllPreferencesAndOnlyKnownFields()
    {
        using var directory = new SettingsDirectory();
        var store = new JsonSettingsStore(Path.Combine(directory.Root, "child", "settings.json"));
        var expected = new AppSettings
        {
            RootFolder = Path.Combine(directory.Root, "搜索目录"), UseAnd = false, MatchCase = true,
            Extensions = [".TXT", ".pptx", ".txt"], ContextCharacters = 500, SortMode = 3,
            EncodingMode = TextEncodingMode.Utf8OrGb18030, AllowOfficeAutomation = true,
            Scope = new(WordBody: false, WordTables: false, ExcelHiddenSheets: true, PresentationText: false, PresentationTables: false)
        };
        Assert.Null(store.Save(expected));
        var result = store.Load();
        Assert.Null(result.Warning);
        Assert.Equal(expected with { Extensions = result.Settings.Extensions }, result.Settings);
        Assert.Equal(new[] { ".txt", ".pptx" }, result.Settings.Extensions);
        Assert.Equal(new[] { ".TXT", ".pptx", ".txt" }, expected.Extensions);
        using var json = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.Equal(new[] { "Version", "RootFolder", "UseAnd", "MatchCase", "Extensions", "ContextCharacters", "SortMode", "EncodingMode", "AllowOfficeAutomation", "Scope" },
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Theory]
    [InlineData(TextEncodingMode.StrictUnicode)]
    [InlineData(TextEncodingMode.Utf8OrGb18030)]
    [InlineData(TextEncodingMode.Gb18030)]
    public void EachEncodingAndMinimumBoundsRoundTrip(TextEncodingMode encoding)
    {
        using var directory = new SettingsDirectory();
        var store = directory.Store();
        Assert.Null(store.Save(new() { ContextCharacters = 0, SortMode = 0, EncodingMode = encoding, Extensions = [] }));
        var result = store.Load();
        Assert.Null(result.Warning);
        Assert.Equal(encoding, result.Settings.EncodingMode);
        Assert.Equal(0, result.Settings.ContextCharacters);
        Assert.Empty(result.Settings.Extensions);
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Version\":2}")]
    [InlineData("{\"Version\":0}")]
    [InlineData("{\"Version\":null}")]
    [InlineData("{\"RootFolder\":null}")]
    [InlineData("{\"RootFolder\":123}")]
    [InlineData("{\"RootFolder\":\"a\\u0000b\"}")]
    [InlineData("{\"Extensions\":null}")]
    [InlineData("{\"Extensions\":[null]}")]
    [InlineData("{\"Extensions\":[\".exe\"]}")]
    [InlineData("{\"Extensions\":[\"txt\"]}")]
    [InlineData("{\"Extensions\":[\"../.txt\"]}")]
    [InlineData("{\"Extensions\":[\"\"]}")]
    [InlineData("{\"EncodingMode\":99}")]
    [InlineData("{\"EncodingMode\":\"arbitrary\"}")]
    [InlineData("{\"ContextCharacters\":-1}")]
    [InlineData("{\"ContextCharacters\":501}")]
    [InlineData("{\"SortMode\":-1}")]
    [InlineData("{\"SortMode\":4}")]
    [InlineData("{\"Scope\":null}")]
    [InlineData("{\"Scope\":{\"WordBody\":\"yes\"}}")]
    [InlineData("{\"UseAnd\":1}")]
    public void InvalidFilesReturnDefaultsAndWarningAndRemainByteForByteUnchanged(string input)
    {
        using var directory = new SettingsDirectory();
        var store = directory.Store();
        File.WriteAllText(store.FilePath, input);
        var bytes = File.ReadAllBytes(store.FilePath);
        var time = File.GetLastWriteTimeUtc(store.FilePath);
        var result = store.Load();
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
        AssertDefaults(result.Settings);
        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
        Assert.Equal(time, File.GetLastWriteTimeUtc(store.FilePath));
        Assert.Single(Directory.GetFileSystemEntries(directory.Root));
    }

    [Fact]
    public void PartialScopeRetainsDefaultsForUnspecifiedRegions()
    {
        using var directory = new SettingsDirectory();
        var store = directory.Store();
        File.WriteAllText(store.FilePath, "{\"Scope\":{\"WordBody\":false}}");
        var result = store.Load();
        Assert.Null(result.Warning);
        Assert.Equal(new DocumentScopeOptions(WordBody: false), result.Settings.Scope);
    }

    [Fact]
    public void ExplicitSaveCanReplacePreviouslyInvalidFile()
    {
        using var directory = new SettingsDirectory();
        var store = directory.Store();
        File.WriteAllText(store.FilePath, "{broken");
        Assert.NotNull(store.Load().Warning);
        Assert.Null(store.Save(new() { MatchCase = true }));
        var result = store.Load();
        Assert.Null(result.Warning);
        Assert.True(result.Settings.MatchCase);
        Assert.Single(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public void UnknownInputFieldsAreNotPersistedAsSearchHistory()
    {
        using var directory = new SettingsDirectory();
        var store = directory.Store();
        File.WriteAllText(store.FilePath, "{\"Terms\":[\"SECRET_KEYWORD_917\"],\"Results\":[\"SECRET_RESULT_918\"],\"DocumentText\":\"SECRET_BODY_919\"}");
        var result = store.Load();
        Assert.Null(result.Warning);
        Assert.Null(store.Save(result.Settings));
        var text = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("Terms", text);
        Assert.DoesNotContain("Results", text);
        Assert.DoesNotContain("DocumentText", text);
        Assert.DoesNotContain("SECRET_", text);
    }

    [Fact]
    public void SaveInvalidValuesWarnsAndPreservesPreviousFile()
    {
        using var directory = new SettingsDirectory();
        var store = directory.Store();
        Assert.Null(store.Save(new()));
        var original = File.ReadAllBytes(store.FilePath);
        AppSettings[] invalid = [
            new() { Version = 2 }, new() { RootFolder = null! }, new() { RootFolder = "a\0b" },
            new() { Extensions = null! }, new() { Extensions = [null!] }, new() { Extensions = [".exe"] },
            new() { ContextCharacters = -1 }, new() { ContextCharacters = 501 },
            new() { SortMode = -1 }, new() { SortMode = 4 }, new() { EncodingMode = (TextEncodingMode)99 },
            new() { Scope = null! }, null!
        ];
        foreach (var settings in invalid)
        {
            Assert.NotNull(store.Save(settings));
            Assert.Equal(original, File.ReadAllBytes(store.FilePath));
            Assert.Single(Directory.GetFileSystemEntries(directory.Root));
        }
    }

    [Fact]
    public void DirectoryTargetProducesReadableIoWarningsAndCleansOwnTemporaryFile()
    {
        using var directory = new SettingsDirectory();
        var target = Path.Combine(directory.Root, "settings.json");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "keep.txt");
        File.WriteAllText(marker, "untouched");
        var otherTemporary = Path.Combine(directory.Root, "unrelated.tmp");
        File.WriteAllText(otherTemporary, "also untouched");
        var store = new JsonSettingsStore(target);
        Assert.False(string.IsNullOrWhiteSpace(store.Load().Warning));
        Assert.False(string.IsNullOrWhiteSpace(store.Save(new())));
        Assert.Equal("untouched", File.ReadAllText(marker));
        Assert.Equal(new[] { otherTemporary }, Directory.GetFiles(directory.Root));
    }

    [Fact]
    public void FileInsteadOfParentDirectoryProducesControlledIoFailure()
    {
        using var directory = new SettingsDirectory();
        var blocker = Path.Combine(directory.Root, "blocker");
        File.WriteAllText(blocker, "preserve");
        var store = new JsonSettingsStore(Path.Combine(blocker, "settings.json"));
        Assert.NotNull(store.Save(new()));
        Assert.Equal("preserve", File.ReadAllText(blocker));
        Assert.Single(Directory.GetFileSystemEntries(directory.Root));
    }

    [Fact]
    public void LockedFileReadProducesControlledIoFailure()
    {
        using var directory = new SettingsDirectory();
        var store = directory.Store();
        File.WriteAllText(store.FilePath, "{}");
        using var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(store.Load().Warning);
    }

    private static void AssertDefaults(AppSettings actual)
    {
        var expected = new AppSettings();
        Assert.Equal(expected.Extensions, actual.Extensions);
        Assert.Equal(expected with { Extensions = actual.Extensions }, actual);
    }

    private sealed class SettingsDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch.Settings.Tests", Guid.NewGuid().ToString("N"));
        public SettingsDirectory() => Directory.CreateDirectory(Root);
        public JsonSettingsStore Store() => new(Path.Combine(Root, "settings.json"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
