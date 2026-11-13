namespace LocalDocSearch.Navigation;

/// <summary>Opens an original then uses a verified viewer search control, never global typing or a clipboard.</summary>
public sealed class WindowsOriginalFileFindAssistant : IOriginalFileFindAssistant
{
    private readonly IFileNavigator _navigator;
    private readonly IDocumentActionFileProbe _files;
    private readonly IFindWindowSessionFactory _sessions;
    private readonly bool _isWindows;
    private static readonly SemaphoreSlim SharedWorkerGate = new(1, 1);
    private readonly SemaphoreSlim _workerGate;
    private readonly TimeSpan _assistanceTimeout;

    public WindowsOriginalFileFindAssistant() : this(new WindowsFileNavigator(), new DocumentActionFileProbe(),
        new BrowserPdfFindSessionFactory(), OperatingSystem.IsWindows()) { }

    internal WindowsOriginalFileFindAssistant(IFileNavigator navigator, IDocumentActionFileProbe files,
        IFindWindowSessionFactory sessions, bool isWindows = true, TimeSpan? assistanceTimeout = null, SemaphoreSlim? workerGate = null)
    {
        (_navigator, _files, _sessions, _isWindows) = (navigator, files, sessions, isWindows);
        _assistanceTimeout = assistanceTimeout ?? TimeSpan.FromSeconds(8);
        _workerGate = workerGate ?? SharedWorkerGate;
    }

    public async Task<FindAssistResult> FindAsync(FindAssistRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = request?.SearchText ?? string.Empty;
        if (request is null) return Failed("查找请求为空。", text);
        if (!NavigationValidation.TryPath(request.FullPath, out var path, out var error)) return Failed(error, text);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048 || text.Any(char.IsControl))
            return Failed("请提供 1 至 2048 个字符的单行查找词。", text);
        if (!_isWindows) return Failed("原文件查找辅助仅支持 Windows。", text);
        var opened = false;
        try
        {
            if (!await _files.IsExistingRegularFileAsync(path, cancellationToken).ConfigureAwait(false))
                return Failed("目标文件不存在、不可访问或路径包含重解析点；请检查原文件路径。", text);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _navigator.LocateAsync(new LocateRequest(path), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Level == LocateLevel.Failed) return Failed(result.Message, text);
            opened = true;
            if (!_workerGate.Wait(0)) return Manual(text, "上一次查找辅助仍在等待查看器响应，请稍后重试。");
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<FindAssistResult> worker;
            try { worker = Task.Run(() => Assist(path, text, stop.Token), CancellationToken.None); }
            catch { stop.Dispose(); _workerGate.Release(); throw; }
            _ = worker.ContinueWith(finished =>
            {
                _ = finished.Exception; // Observe a late native failure after cancellation/timeout.
                stop.Dispose();
                _workerGate.Release(); // A timed-out native call retains the slot until it really exits.
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { return await worker.WaitAsync(_assistanceTimeout, cancellationToken).ConfigureAwait(false); }
            finally
            {
                try { stop.Cancel(); }
                catch (ObjectDisposedException) { } // Worker has already finished and released its slot.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return opened ? Manual(text, $"查找辅助未完成（{exception.Message}）。") : Failed($"打开原文件未完成：{exception.Message}", text);
        }
    }

    private FindAssistResult Assist(string path, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = _sessions.TryCreate(path, token);
        if (session is null) return Manual(text, "当前默认查看器未提供可核验的原文件窗口和查找控件。");
        token.ThrowIfCancellationRequested();
        var target = session.ReadTarget();
        if (!ValidTarget(target, path)) return Manual(text, "原文件完整路径、查看器或前台窗口核验未通过。");
        token.ThrowIfCancellationRequested();
        if (!SameTarget(target, session.ReadTarget()) || !session.OpenFind(token))
            return Manual(text, "目标窗口发生变化或查看器未提供可核验的查找操作。");
        token.ThrowIfCancellationRequested();
        var field = session.ReadSearchField();
        if (!ValidField(field, target!) || !SameTarget(target, session.ReadTarget()))
            return Manual(text, "查找输入框的身份、焦点或可编辑状态核验未通过。");
        token.ThrowIfCancellationRequested();
        // The adapter revalidates target + exact element immediately before its targeted ValuePattern call.
        if (!session.SetSearchValue(field!, text, token)) return Manual(text, "查找输入框发生变化，已停止自动填词。");
        token.ThrowIfCancellationRequested();
        var readBack = session.ReadSearchField();
        if (!SameTarget(target, session.ReadTarget()) || !ValidField(readBack, target!) ||
            readBack!.RuntimeId != field!.RuntimeId || readBack.Value != text)
            return Manual(text, "查找词未通过同一输入框读回核验，请检查查看器。");
        return new(FindAssistStatus.SearchFieldFilled, "已核实原 PDF 窗口和查找输入框，并填入查找词；未确认命中结果或原文选区。", text);
    }

    private static bool ValidTarget(FindTargetEvidence? target, string path) => target is
        { Window: not 0, ProcessId: > 0, IsForeground: true, SupportedViewer: true } && NavigationValidation.SamePath(path, target.FullPath);
    private static bool SameTarget(FindTargetEvidence? expected, FindTargetEvidence? actual) =>
        expected is not null && actual is not null && actual == expected && actual.IsForeground;
    private static bool ValidField(FindSearchFieldEvidence? field, FindTargetEvidence target) => field is
        { IsSearch: true, IsFocused: true, IsEditable: true, IsEnabled: true } &&
        !string.IsNullOrEmpty(field.RuntimeId) && field.Window == target.Window && field.ProcessId == target.ProcessId;
    private static FindAssistResult Failed(string message, string text) => new(FindAssistStatus.Failed, message, text);
    private static FindAssistResult Manual(string text, string reason) => new(FindAssistStatus.ManualFindRequired,
        $"{reason} 已请求打开原文件；查看器首次启动时请等待文件显示后重试。请检查该文件窗口的查找框；必要时按 Ctrl+F，使用应用内的“复制查找词”后手动粘贴。本操作未使用或更改剪贴板。", text);
}

internal sealed record FindTargetEvidence(nint Window, int ProcessId, string FullPath, bool IsForeground, bool SupportedViewer);
internal sealed record FindSearchFieldEvidence(nint Window, int ProcessId, string RuntimeId,
    bool IsSearch, bool IsFocused, bool IsEditable, bool IsEnabled, string Value);
internal interface IFindWindowSessionFactory
{
    IFindWindowSession? TryCreate(string fullPath, CancellationToken token);
}
internal interface IFindWindowSession : IDisposable
{
    FindTargetEvidence? ReadTarget();
    bool OpenFind(CancellationToken token);
    FindSearchFieldEvidence? ReadSearchField();
    bool SetSearchValue(FindSearchFieldEvidence expectedField, string text, CancellationToken token);
}
