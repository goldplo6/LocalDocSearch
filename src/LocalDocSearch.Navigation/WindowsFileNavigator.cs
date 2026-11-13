using LocalDocSearch.Core;

namespace LocalDocSearch.Navigation;

/// <summary>Windows file navigation; construction performs no I/O and starts no Office process.</summary>
public sealed class WindowsFileNavigator : IFileNavigator
{
    private readonly INavigationBackend _backend;

    public WindowsFileNavigator() : this(new WindowsNavigationBackend()) { }

    internal WindowsFileNavigator(INavigationBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<LocateResult> LocateAsync(LocateRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null) return Failed("打开请求为空。");
        if (!NavigationValidation.TryPath(request.FullPath, out var fullPath, out var error)) return Failed(error);
        if (!_backend.IsWindows) return Failed("文件定位仅支持 Windows；未启动应用。");

        try
        {
            if (!await _backend.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
                return Failed("目标文件不存在或当前不可访问。");
            cancellationToken.ThrowIfCancellationRequested();

            var reason = request.AllowOfficeAutomation
                ? "此格式或来源没有可验证的 Office 定位，已请求默认应用打开。"
                : "Office 自动化未启用；已请求默认应用打开，未验证命中位置。";
            if (Path.GetExtension(fullPath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
                reason = request.AllowOfficeAutomation
                    ? "已请求默认应用打开 Word；段落和表格编号仅为提取结构提示，来源信息未满足原生文字选区核验条件。"
                    : "Office 自动化未启用；已请求默认应用打开 Word，未验证文字位置或页码。";

            if (Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                reason = "已请求默认 PDF 应用打开；当前未验证页码或文字选区。";

            if (request.AllowOfficeAutomation && TryTarget(fullPath, request.Match, out var target))
            {
                try
                {
                    var evidence = await _backend.LocateOfficeAsync(target!, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (evidence.Opened) return Grade(target!, evidence);
                    reason = $"{evidence.Message} 已回退默认应用打开，未验证命中位置。";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is OfficeTimeoutException or OfficeBusyException)
                {
                    // The previous COM call may still be active. Do not dispatch a duplicate open.
                    return Failed(exception.Message);
                }
                catch (Exception exception)
                {
                    reason = $"Office 定位未完成（{exception.Message}）；已回退默认应用打开，未验证命中位置。";
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _backend.OpenShellAsync(fullPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new LocateResult(LocateLevel.OpenedOnly, reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return Failed($"打开文件未完成：{exception.Message}");
        }
    }

    private static LocateResult Grade(OfficeTarget target, OfficeLocationEvidence evidence)
    {
        if (!NavigationValidation.SamePath(target.FullPath, evidence.DocumentPath))
            return Failed($"Office 返回的完整路径未通过目标文件核验；未确认定位。{evidence.Message}");

        if (target.Kind == OfficeTargetKind.ExcelCell &&
            string.Equals(target.Sheet, evidence.Sheet, StringComparison.Ordinal) &&
            NavigationValidation.TryAddress(evidence.Address, out var address) && address == target.Address)
            return new LocateResult(LocateLevel.Exact, $"已核实 Excel 工作簿、工作表和选区：{target.Sheet}!{target.Address}。{evidence.Message}");

        if (target.TextTarget is { } textTarget && evidence.TextSelection is { } selection &&
            (target.Kind == OfficeTargetKind.WordText ||
                target.Kind == OfficeTargetKind.PowerPointSlide && evidence.Slide == target.Slide) &&
            OfficeTextNavigation.MatchesSelection(textTarget, selection))
            return new LocateResult(LocateLevel.Exact, $"已核实 {(target.Kind == OfficeTargetKind.WordText ? "Word" : "PowerPoint")} 来源段落、上下文和实际文字选区。{evidence.Message}");

        if (target.Kind == OfficeTargetKind.PowerPointSlide && evidence.Slide == target.Slide)
            return new LocateResult(LocateLevel.Section, $"已核实 PowerPoint 第 {target.Slide} 张幻灯片；未定位或验证具体文字。{evidence.Message}");

        return new LocateResult(LocateLevel.OpenedOnly, $"文件已打开，但请求位置未通过读回核验。{evidence.Message}");
    }

    private static bool TryTarget(string path, MatchItem? match, out OfficeTarget? target)
    {
        target = null;
        if (match?.Location is not { } location) return false;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && location.Kind == SourceKind.ExcelCell &&
            NavigationValidation.ValidSheet(location.Sheet) && NavigationValidation.TryAddress(location.Address, out var address))
            target = new OfficeTarget(path, OfficeTargetKind.ExcelCell, location.Sheet, address);
        else if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) &&
            location.Kind is SourceKind.SlideText or SourceKind.SlideTableCell && location.Slide is > 0)
        {
            OfficeTextNavigation.TryCreate(match, out var textTarget);
            target = new OfficeTarget(path, OfficeTargetKind.PowerPointSlide, Slide: location.Slide) { TextTarget = textTarget };
        }
        else if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) &&
            OfficeTextNavigation.TryCreate(match, out var wordTarget) && location.Kind == SourceKind.WordParagraph)
            target = new OfficeTarget(path, OfficeTargetKind.WordText) { TextTarget = wordTarget };
        return target is not null;
    }

    private static LocateResult Failed(string message) => new(LocateLevel.Failed, message);
}

internal static class NavigationValidation
{
    // ShellExecute receives only a filename, never a command, executable, URL or argument string.
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".log", ".csv", ".docx", ".xlsx", ".pptx", ".pdf" };

    internal static bool TryPath(string? path, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = "请提供现有文档的完整文件路径。";
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0 ||
            path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path)) return false;
        try
        {
            // Reject NTFS alternate data streams and URI/shell namespaces. A drive colon is the only allowed colon.
            var start = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? 2 : 0;
            if (path.AsSpan(start).Contains(':')) return false;
            fullPath = Path.GetFullPath(path);
            if (!SupportedExtensions.Contains(Path.GetExtension(fullPath)))
            {
                error = "仅打开受支持的文档格式（txt、md、log、csv、docx、xlsx、pptx、pdf）。";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool SamePath(string expected, string? actual)
    {
        if (actual is null || !TryPath(actual, out var normalized, out _)) return false;
        return string.Equals(expected, normalized, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ValidSheet(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 31 &&
        name.IndexOfAny(['[', ']', ':', '\\', '/', '?', '*', '\0', '\r', '\n']) < 0;

    internal static bool TryAddress(string? address, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(address) || address.Length > 12) return false;
        var index = address[0] == '$' ? 1 : 0;
        var letters = index;
        var column = 0;
        while (index < address.Length && char.IsAsciiLetter(address[index]))
        {
            column = column * 26 + char.ToUpperInvariant(address[index]) - 'A' + 1;
            index++;
            if (index - letters > 3) return false;
        }
        if (index == letters || column > 16384) return false;
        var columnText = address[letters..index].ToUpperInvariant();
        if (index < address.Length && address[index] == '$') index++;
        var rowStart = index;
        if (index == address.Length || address[index] is < '1' or > '9') return false;
        var row = 0;
        while (index < address.Length && address[index] is >= '0' and <= '9')
        {
            row = row * 10 + address[index++] - '0';
            if (row > 1048576) return false;
        }
        if (index != address.Length || index == rowStart) return false;
        normalized = columnText + row.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}
