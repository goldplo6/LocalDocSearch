using System.Security;
using System.Text.Json;
using LocalDocSearch.Core;

namespace LocalDocSearch.Settings;

/// <summary>Reads without modifying the file; explicit saves atomically replace it in its own directory.</summary>
public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".log", ".csv", ".docx", ".xlsx", ".pptx", ".pdf" };

    public JsonSettingsStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalDocSearch", "settings.json");

    public string FilePath { get; }

    public SettingsLoadResult Load()
    {
        try
        {
            using var stream = File.OpenRead(Path.GetFullPath(FilePath));
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            var warning = Validate(settings, out var validated);
            return warning is null ? new(validated!) : new(new(), $"设置无效，已使用默认设置；原文件已保留，显式保存会替换原文件。{warning}");
        }
        catch (FileNotFoundException) { return new(new()); }
        catch (DirectoryNotFoundException) { return new(new()); }
        catch (JsonException) { return new(new(), "设置 JSON 已损坏或类型无效，已使用默认设置；原文件已保留，显式保存会替换原文件。"); }
        catch (Exception exception) when (IsFileError(exception))
        {
            return new(new(), $"读取设置失败，已使用默认设置；原文件未更改。{exception.Message}");
        }
    }

    /// <summary>Returns null on success. Invalid preferences and ordinary file errors return a warning.</summary>
    public string? Save(AppSettings settings)
    {
        var warning = Validate(settings, out var validated);
        if (warning is not null) return $"设置未保存。{warning}";
        string? temporaryPath = null;
        var ownsTemporaryFile = false;
        try
        {
            var target = Path.GetFullPath(FilePath);
            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporaryFile = true;
                JsonSerializer.Serialize(stream, validated!, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, target, overwrite: true);
            ownsTemporaryFile = false;
            return null;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return $"保存设置失败，请检查目录权限、文件占用及磁盘空间。{exception.Message}";
        }
        finally
        {
            if (ownsTemporaryFile && temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (IsFileError(exception)) { /* Best effort, only our own file. */ }
            }
        }
    }

    private static string? Validate(AppSettings? settings, out AppSettings? validated)
    {
        validated = null;
        if (settings is null) return "设置对象为空。";
        if (settings.Version != 1) return $"不支持设置版本 {settings.Version}（当前版本 1）。";
        if (settings.RootFolder is null || settings.RootFolder.Contains('\0')) return "搜索目录必须为不含空字符的字符串。";
        if (!Enum.IsDefined(settings.EncodingMode)) return "文本编码选项无效。";
        if (settings.ContextCharacters is < 0 or > 500) return "上下文字符数应在 0 至 500 之间。";
        if (settings.SortMode is < 0 or > 3) return "排序选项应在 0 至 3 之间。";
        if (settings.Scope is null) return "文档范围设置为空。";
        if (settings.Extensions is null || settings.Extensions.Any(extension => extension is null || !AllowedExtensions.Contains(extension)))
            return "文件类型应从 txt、md、log、csv、docx、xlsx、pptx、pdf 中选择，并带点前缀。";
        validated = settings with { Extensions = settings.Extensions.Select(extension => extension.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray() };
        return null;
    }

    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException;
}
