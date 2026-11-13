using System.Threading.Channels;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using LocalDocSearch.Navigation;
using LocalDocSearch.Settings;

namespace LocalDocSearch.Desktop.ViewModels;

public enum SearchControlState { Idle, Running, PauseRequested, Paused, Stopping }

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly JsonSettingsStore? _settingsStore;
    private readonly IFileNavigator _navigator;
    private readonly IOriginalFileFindAssistant _findAssistant;
    private readonly IContainingFolderRevealer _folderRevealer;
    private DocumentScopeOptions _scope = new();
    private bool _suppressAutomaticSave;
    [ObservableProperty] private string settingsWarning = string.Empty;

    private readonly SearchResourceLimits _resourceLimits;
    private readonly int _maxConcurrency;

    public MainWindowViewModel(JsonSettingsStore? settingsStore = null, IFileNavigator? navigator = null, SearchResourceLimits? resourceLimits = null, IOriginalFileFindAssistant? findAssistant = null, IContainingFolderRevealer? folderRevealer = null, int maxConcurrency = 1)
    {
        if (maxConcurrency is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency, "实验并发数仅支持 1、2 或 4。");
        _maxConcurrency = maxConcurrency;
        _resourceLimits = resourceLimits ?? new SearchResourceLimits();
        _settingsStore = settingsStore;
        _navigator = navigator ?? new WindowsFileNavigator();
        _findAssistant = findAssistant ?? new WindowsOriginalFileFindAssistant();
        _folderRevealer = folderRevealer ?? new WindowsContainingFolderRevealer();
        Terms = new QueryTermsCollection(() => !_synchronizingGroupedTerms && (HasGroupedQuery || IsRegularExpression || IsBusy),
            () => StatusText = IsBusy ? "搜索进行中；请在搜索结束后编辑关键词。" : QueryChipHint);
        Terms.CollectionChanged += (_, _) => HandleTermsChanged();
        if (_settingsStore is not null)
        {
            var loaded = _settingsStore.Load();
            ApplySettings(loaded.Settings);
            if (loaded.Warning is not null)
            {
                StatusText = SettingsWarning = loaded.Warning;
                _suppressAutomaticSave = true;
            }
        }
    }

    [ObservableProperty] private int contextCharacters = 60;
    [ObservableProperty] private int encodingIndex;
    [ObservableProperty] private int sortMode;
    [ObservableProperty] private bool allowOfficeAutomation;
    [ObservableProperty] private bool isLocating;

    public string SettingsSummary => $"编码：{EncodingNames[Math.Clamp(EncodingIndex, 0, 2)]} · 上下文 {ContextCharacters} 字符 · {SortNames[Math.Clamp(SortMode, 0, 3)]} · Office 定位{(AllowOfficeAutomation ? "开启" : "关闭")}";
    public static IReadOnlyList<string> EncodingNames { get; } = ["严格 Unicode（默认）", "UTF-8 优先，失败后 GB18030", "无 BOM 强制 GB18030"];
    public static IReadOnlyList<string> SortNames { get; } = ["扫描顺序", "文件路径", "匹配次数最多", "修改时间最新"];

    public AppSettings CaptureSettings() => new()
    {
        RootFolder = RootFolder, UseAnd = UseAnd, MatchCase = MatchCase,
        Extensions = SelectedExtensions().ToArray(), ContextCharacters = ContextCharacters,
        SortMode = SortMode, EncodingMode = (TextEncodingMode)EncodingIndex,
        AllowOfficeAutomation = AllowOfficeAutomation, Scope = _scope
    };

    public void ApplySettings(AppSettings settings, bool save = false)
    {
        RootFolder = settings.RootFolder; UseAnd = settings.UseAnd; MatchCase = settings.MatchCase;
        IncludeTxt = settings.Extensions.Contains(".txt"); IncludeMd = settings.Extensions.Contains(".md");
        IncludeLog = settings.Extensions.Contains(".log"); IncludeCsv = settings.Extensions.Contains(".csv");
        IncludeDocx = settings.Extensions.Contains(".docx"); IncludeXlsx = settings.Extensions.Contains(".xlsx");
        IncludePptx = settings.Extensions.Contains(".pptx"); IncludePdf = settings.Extensions.Contains(".pdf");
        ContextCharacters = Math.Clamp(settings.ContextCharacters, 0, 500);
        EncodingIndex = Math.Clamp((int)settings.EncodingMode, 0, 2);
        SortMode = Math.Clamp(settings.SortMode, 0, 3);
        AllowOfficeAutomation = settings.AllowOfficeAutomation; _scope = settings.Scope;
        OnPropertyChanged(nameof(SettingsSummary));
        SortResults();
        if (save) SaveSettings(explicitSave: true);
    }

    public void SaveSettings(bool explicitSave = false)
    {
        if (_settingsStore is null || (_suppressAutomaticSave && !explicitSave)) return;
        var warning = _settingsStore.Save(CaptureSettings());
        SettingsWarning = warning ?? string.Empty;
        if (warning is not null) StatusText = warning;
        else _suppressAutomaticSave = false;
    }

    private List<string> SelectedExtensions()
    {
        var extensions = new List<string>();
        if (IncludeTxt) extensions.Add(".txt"); if (IncludeMd) extensions.Add(".md");
        if (IncludeLog) extensions.Add(".log"); if (IncludeCsv) extensions.Add(".csv");
        if (IncludeDocx) extensions.Add(".docx"); if (IncludeXlsx) extensions.Add(".xlsx");
        if (IncludePptx) extensions.Add(".pptx"); if (IncludePdf) extensions.Add(".pdf");
        return extensions;
    }

    private void SortResults()
    {
        if (SortMode == 0 || Results.Count < 2) return;
        var sorted = (SortMode switch
        {
            2 => Results.OrderByDescending(f => f.TotalOccurrences).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase),
            3 => Results.OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase),
            _ => Results.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
        }).ToArray();
        ((ResettableObservableCollection<FileResultViewModel>)Results).ReplaceAll(sorted);
    }
    private Channel<FileResult>? _pendingResults;
    private Channel<SearchIssue>? _pendingIssues;
    private SearchProgress? _latestProgress;
    private CancellationTokenSource? _searchCancellation;
    private SearchExecutionControl? _searchExecutionControl;
    private int _visibleOccurrences;

    [ObservableProperty] private string rootFolder = string.Empty;
    [ObservableProperty] private string pendingTerm = string.Empty;
    [ObservableProperty] private bool useAnd = true;
    [ObservableProperty] private bool matchCase;
    [ObservableProperty] private bool includeTxt = true;
    [ObservableProperty] private bool includeMd = true;
    [ObservableProperty] private bool includeLog = true;
    [ObservableProperty] private bool includeCsv = true;
    [ObservableProperty] private bool includeDocx = true;
    [ObservableProperty] private bool includeXlsx = true;
    [ObservableProperty] private bool includePptx = true;
    [ObservableProperty] private bool includePdf = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(PauseResumeSearchCommand), nameof(StopSearchCommand), nameof(CancelSearchCommand))]
    private bool isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PauseResumeSearchText))]
    [NotifyCanExecuteChangedFor(nameof(PauseResumeSearchCommand), nameof(StopSearchCommand), nameof(CancelSearchCommand))]
    private SearchControlState searchState = SearchControlState.Idle;
    [ObservableProperty] private string statusText = "请选择目录并添加关键词。支持八种格式；展开“搜索范围”查看提取规则。";
    [ObservableProperty] private string progressText = "未开始 · 本地只读搜索 · 无全文缓存";

    public bool IsIdle => !IsBusy;
    public string PauseResumeSearchText => SearchState is SearchControlState.PauseRequested or SearchControlState.Paused ? "继续" : "暂停";
    private bool CanControlSearch => IsBusy && _searchExecutionControl is not null &&
        _searchCancellation is { IsCancellationRequested: false } &&
        SearchState is SearchControlState.Running or SearchControlState.PauseRequested or SearchControlState.Paused;
    public bool UseOr { get => !UseAnd; set => UseAnd = !value; }
    public ObservableCollection<string> Terms { get; }
    public ObservableCollection<FileResultViewModel> Results { get; } = new ResettableObservableCollection<FileResultViewModel>();
    public ObservableCollection<SearchIssue> Issues { get; } = [];

    partial void OnUseAndChanged(bool value)
    {
        OnPropertyChanged(nameof(UseOr));
        RefreshQueryChips();
    }

    [RelayCommand]
    private void AddTerm()
    {
        if (IsBusy) return;
        var term = PendingTerm.Trim();
        if (term.Length == 0) return;
        if (IsRegularExpression) { StatusText = QueryChipHint; return; }
        if (HasGroupedQuery)
        {
            if (TryAddGroupedTerm(term)) PendingTerm = string.Empty;
            return;
        }
        if (!Terms.Contains(term, StringComparer.Ordinal))
        {
            if (Terms.Count >= _resourceLimits.MaxTerms)
            {
                StatusText = $"关键词数量须不超过 {_resourceLimits.MaxTerms}。";
                return;
            }
            try
            {
                // Check the candidate before changing either Terms or its saved adjacent links.
                var connectors = CaptureTermConnectors().Concat(Terms.Count == 0 ? Array.Empty<SearchOperator>()
                    : new[] { UseAnd ? SearchOperator.And : SearchOperator.Or }).ToArray();
                _ = new QueryEditorModel(Terms.Append(term).ToArray(), connectors, limits: _resourceLimits);
            }
            catch (ArgumentException ex) { StatusText = ex.Message; return; }
            if (Terms.Count > 0) _termLinks[term] = UseAnd ? SearchOperator.And : SearchOperator.Or;
            Terms.Add(term);
        }
        PendingTerm = string.Empty;
    }

    [RelayCommand]
    private void RemoveTerm(string? term)
    {
        if (IsBusy || term is null) return;
        if (HasGroupedQuery) { StatusText = QueryChipHint; return; }
        Terms.Remove(term);
    }

    [RelayCommand]
    private async Task StartSearchAsync()
    {
        if (IsBusy) return;
        AddTerm();
        if (!string.IsNullOrWhiteSpace(PendingTerm)) return;
        var extensions = SelectedExtensions();
        if (string.IsNullOrWhiteSpace(RootFolder))
        {
            StatusText = "请选择一个存在且可访问的目录。";
            return;
        }
        if (!IsRegularExpression && Terms.Count == 0) { StatusText = "请先添加至少一个关键词。"; return; }
        if (extensions.Count == 0) { StatusText = "请至少勾选一种文件类型。"; return; }

        SearchRequest request;
        try { request = CaptureSearchRequest(extensions); }
        catch (ArgumentException ex) { StatusText = ex.Message; return; }
        _resultEncodingMode = request.EncodingMode;
        _resultScope = _scope;
        var engine = new TextSearchEngine(DocumentExtractorCatalog.CreateDefault(_scope), _resourceLimits);
        SaveSettings();
        Results.Clear();
        Issues.Clear();
        var resultChannel = CreateBoundedChannel<FileResult>(PendingResultCapacity);
        var issueChannel = CreateBoundedChannel<SearchIssue>(PendingIssueCapacity);
        _pendingResults = resultChannel;
        _pendingIssues = issueChannel;
        _latestProgress = null;
        _visibleOccurrences = 0;
        ResetSearchDiagnostics();
        using var cancellation = new CancellationTokenSource();
        var executionControl = new SearchExecutionControl();
        _searchCancellation = cancellation;
        _searchExecutionControl = executionControl;
        SearchState = SearchControlState.Running;
        StatusText = IsRegularExpression ? "正在按正则匹配搜索；独立提取片段之间不拼接，零长度匹配跳过。"
            : "正在搜索；按顶部公式在同一文件内判断，AND 优先于 OR。";
        ProgressText = "正在扫描目录…";
        IsBusy = true;
        var drainScheduled = 0;
        void RequestBackpressureDrain()
        {
            // Only a full channel requests an early UI slice. Coalesce both channels
            // into one queued callback; do not create a self-posting drain loop.
            if (Interlocked.CompareExchange(ref drainScheduled, 1, 0) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // A queued callback belongs only to this run, including cleanup.
                    if (!ReferenceEquals(_pendingResults, resultChannel)) return;
                    _backpressureUiWakeups++;
                    ObserveUiRefresh();
                    DrainPending(100);
                    RefreshSearchControlState();
                }
                finally { Interlocked.Exchange(ref drainScheduled, 0); }
            }, DispatcherPriority.Background);
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_pendingResults, resultChannel)) return;
            ObserveUiRefresh();
            DrainPending(100);
            RefreshSearchControlState();
        };
        timer.Start();
        try
        {
            var progress = new CallbackProgress<SearchProgress>(p => Interlocked.Exchange(ref _latestProgress, p));
            var results = new CallbackProgress<FileResult>(file =>
            {
                if (cancellation.IsCancellationRequested) return;
                // This callback runs on the search worker, never on the UI thread. Wait
                // applies backpressure without dropping complete files; cancellation unblocks it.
                if (!resultChannel.Writer.TryWrite(file))
                {
                    _resultBackpressureWaitCount++;
                    var started = Stopwatch.GetTimestamp();
                    RequestBackpressureDrain();
                    try { resultChannel.Writer.WriteAsync(file, cancellation.Token).AsTask().GetAwaiter().GetResult(); }
                    finally { _resultBackpressureWaitMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
                }
                _maximumPendingResults = Math.Max(_maximumPendingResults, resultChannel.Reader.Count);
            });
            var issues = new CallbackProgress<SearchIssue>(issue =>
            {
                if (!issueChannel.Writer.TryWrite(issue))
                {
                    // Diagnostic delivery keeps draining after cancellation, including the
                    // final aggregate notice. The UI timer remains active until the worker ends.
                    _issueBackpressureWaitCount++;
                    var started = Stopwatch.GetTimestamp();
                    RequestBackpressureDrain();
                    try { issueChannel.Writer.WriteAsync(issue).AsTask().GetAwaiter().GetResult(); }
                    finally { _issueBackpressureWaitMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
                }
                _maximumPendingIssues = Math.Max(_maximumPendingIssues, issueChannel.Reader.Count);
            });
            // Validation includes filesystem metadata, so keep it off the UI thread too.
            var summary = await Task.Run(() => engine.SearchAsync(request, progress, results, issues, cancellation.Token, executionControl, _maxConcurrency));
            await DrainCompletedSearchAsync(executionControl, cancellation.Token);
            SortResults();
            // Sorting can notify observers too; a pause requested during the final reset
            // still belongs to this run, even though the worker has already returned.
            await DrainCompletedSearchAsync(executionControl, cancellation.Token);
            ProgressText = $"已扫描 {summary.Progress.ScannedFiles:N0} · 已展示文件 {Results.Count:N0} · 已展示匹配 {_visibleOccurrences:N0} · 跳过/失败 {summary.Progress.SkippedFiles:N0}";
            var finalStatus = cancellation.IsCancellationRequested ? SearchStatus.Cancelled : summary.Status;
            CompleteSearchDiagnostics(summary with { Status = finalStatus });
            StatusText = finalStatus switch
            {
                SearchStatus.Completed => "已完成。双击结果定位并选中命中文字；说明与诊断见 !。", 
                SearchStatus.Cancelled => "已停止；保留已展示的完整文件结果，本次扫描未完成。",
                SearchStatus.Partial => "部分完成；存在提取警告或读取失败，请展开说明列表。",
                SearchStatus.Limited => "达到资源上限；保留已完整展示的文件，本次扫描未完成。请缩小目录或提高关键词精度后重试。",
                SearchStatus.Failed => "搜索失败；请查看错误列表与目录权限。",
                _ => "搜索已结束。"
            };
        }
        catch (ArgumentException ex)
        {
            StatusText = ex.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止；保留已展示的完整文件结果，本次扫描未完成。";
        }
        catch (Exception ex)
        {
            StatusText = $"搜索发生错误：{ex.Message}";
        }
        finally
        {
            timer.Stop();
            // Received issues survive cancellation and unexpected failures. Complete
            // file results still in transit are displayed only when not cancelled.
            while (HasPendingItems())
            {
                DrainPending(100);
                await Task.Delay(1);
            }
            resultChannel.Writer.TryComplete();
            issueChannel.Writer.TryComplete();
            _pendingResults = null;
            _pendingIssues = null;
            _searchCancellation = null;
            executionControl.Resume();
            _searchExecutionControl = null;
            _latestProgress = null;
            SearchState = SearchControlState.Idle;
            if (!HasDiagnostics)
            {
                _uiSearchClock?.Stop();
                DiagnosticsText = "本次搜索未生成完整统计；请参考状态说明。";
            }
            IsBusy = false;
        }
    }

    private bool HasPendingItems() =>
        (_pendingResults?.Reader.TryPeek(out _) ?? false) || (_pendingIssues?.Reader.TryPeek(out _) ?? false);

    private async Task DrainCompletedSearchAsync(SearchExecutionControl executionControl, CancellationToken token)
    {
        while (true)
        {
            // A pause request must never block delivery from a full bounded channel:
            // the producer needs that space before it can reach its next pause gate.
            DrainPending(100);
            RefreshSearchControlState();
            if (!HasPendingItems())
            {
                if (!token.IsCancellationRequested && executionControl.IsPauseRequested)
                {
                    // The worker may already have finished. Park this completion phase
                    // in the same gate so the last result cannot turn Paused into Completed.
                    var pause = executionControl.WaitIfPausedAsync(token);
                    RefreshSearchControlState();
                    try { await pause; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    continue;
                }
                return;
            }
            await Task.Delay(1);
        }
    }

    private void RefreshSearchControlState()
    {
        var control = _searchExecutionControl;
        if (!CanControlSearch || control is null || !control.IsPauseRequested) return;
        if (control.IsPaused && !HasPendingItems() && SearchState != SearchControlState.Paused)
        {
            // Draining can outlive the last throttled worker progress report. Publish
            // the actual displayed counts before acknowledging the paused state.
            ProgressText = $"扫描已暂停 · 已展示文件 {Results.Count:N0} · 已展示匹配 {_visibleOccurrences:N0}";
            StatusText = "已暂停；继续将沿用本次查询与已展示结果。";
            SearchState = SearchControlState.Paused;
        }
    }

    private void DrainPending(int maximum)
    {
        var frame = Stopwatch.GetTimestamp();
        if (_searchCancellation?.IsCancellationRequested == true)
        {
            while (_pendingResults?.Reader.TryRead(out _) == true) { }
        }
        else
        {
            // Reserve half of each time slice for diagnostics, even under result load.
            for (var i = 0; i < maximum && Stopwatch.GetElapsedTime(frame).TotalMilliseconds < 4 &&
                _searchCancellation?.IsCancellationRequested != true &&
                _pendingResults?.Reader.TryRead(out var file) == true; i++)
            {
                Results.Add(new FileResultViewModel(file));
                _visibleOccurrences += file.TotalOccurrences;
            }
        }
        for (var i = 0; i < maximum && Stopwatch.GetElapsedTime(frame).TotalMilliseconds < 8 &&
            _pendingIssues?.Reader.TryRead(out var issue) == true; i++) Issues.Add(issue);
        var progress = Interlocked.Exchange(ref _latestProgress, null);
        if (progress is not null && _searchCancellation?.IsCancellationRequested != true && SearchState != SearchControlState.Stopping)
            ProgressText = $"已扫描 {progress.ScannedFiles:N0} · 已发现文件 {progress.MatchedFiles:N0} · 匹配 {progress.Occurrences:N0} · 已展示 {Results.Count:N0} · 跳过/失败 {progress.SkippedFiles:N0}";
    }

    [RelayCommand(CanExecute = nameof(CanControlSearch))]
    private void PauseResumeSearch()
    {
        if (!CanControlSearch || _searchExecutionControl is not { } control) return;
        if (control.IsPauseRequested)
        {
            control.Resume();
            StatusText = "正在继续搜索；沿用本次查询与已展示结果。";
            SearchState = SearchControlState.Running;
        }
        else
        {
            control.Pause();
            StatusText = "正在暂停；等待当前提取检查点，并展示已接收的完整文件结果…";
            SearchState = SearchControlState.PauseRequested;
        }
    }

    [RelayCommand(CanExecute = nameof(CanControlSearch))]
    private void StopSearch()
    {
        if (!CanControlSearch || _searchCancellation is not { } cancellation) return;
        Interlocked.CompareExchange(ref _cancelRequestedTimestamp, Stopwatch.GetTimestamp(), 0);
        StatusText = "正在停止；等待当前提取检查点，保留已展示的完整文件结果…";
        ProgressText = $"正在停止 · 已展示 {Results.Count:N0} 个完整文件 · 丢弃待展示结果";
        SearchState = SearchControlState.Stopping;
        cancellation.Cancel();
        // Also releases a worker or completion phase already waiting at the pause gate.
        _searchExecutionControl?.Resume();
    }

    // Preserve the established command for window closing and existing callers/tests.
    [RelayCommand(CanExecute = nameof(CanControlSearch))]
    private void CancelSearch() => StopSearch();

    [RelayCommand]
    private void LoadDemo()
    {
        if (IsBusy) return;
        Issues.Clear();
        _uiSearchClock?.Stop();
        LastSearchSummary = null;
        HasDiagnostics = false;
        DiagnosticsText = "当前为模拟数据；性能诊断仅对实际搜索生成。";
        var demo = new List<FileResultViewModel>(10_000);
        for (var i = 1; i <= 10_000; i++)
        {
            var matches = new[] { new MatchItem("西瓜", "这是用于界面验证的", "西瓜", "模拟上下文。", new SourceLocation(i, 12)) };
            demo.Add(new FileResultViewModel(new FileResult(
                Path.Combine("模拟数据（非真实文件）", $"示例_{i:00000}.txt"), ".txt", DateTime.UtcNow, matches)));
        }
        ((ResettableObservableCollection<FileResultViewModel>)Results).ReplaceAll(demo);
        _visibleOccurrences = 10_000;
        StatusText = "界面演示：10,000 条模拟文件结果，未扫描磁盘；不代表真实性能基线。";
        ProgressText = "模拟数据 · 仅检查列表虚拟化、滚动、展开与高亮";
    }

    [RelayCommand]
    private Task OpenFileAsync(FileResultViewModel? file) => file?.FirstMatch is null
        ? Task.CompletedTask : FindOriginalAsync(file.FullPath, file.FirstMatch.Text);

    [RelayCommand]
    private Task LocateFileAsync(FileResultViewModel? file) => file is null
        ? Task.CompletedTask : NavigateAsync(file.FullPath, file.FirstMatch);

    [RelayCommand]
    private Task LocateMatchAsync(MatchViewModel? match) => match?.FullPath is null
        ? Task.CompletedTask : NavigateAsync(match.FullPath, match.SourceMatch);

    private async Task NavigateAsync(string path, MatchItem? match)
    {
        if (IsLocating) return;
        IsLocating = true;
        try
        {
            StatusText = "正在请求打开并核实定位…";
            var result = await _navigator.LocateAsync(new LocateRequest(path, match, AllowOfficeAutomation));
            StatusText = $"定位级别：{result.Level} · {result.Message}";
        }
        catch (Exception ex) { StatusText = $"定位级别：Failed · {ex.Message}"; }
        finally { IsLocating = false; }
    }

    [RelayCommand]
    private async Task RevealFileAsync(FileResultViewModel? file)
    {
        if (file is null) return;
        try
        {
            var result = await _folderRevealer.RevealAsync(file.FullPath);
            StatusText = result.Succeeded ? $"所在目录：{result.Message}" : $"打开目录失败：{result.Message}";
        }
        catch (Exception ex) { StatusText = $"打开目录失败：{ex.Message}"; }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
