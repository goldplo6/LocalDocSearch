using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalDocSearch.Core;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int PendingResultCapacity = 64;
    private const int PendingIssueCapacity = 256;
    private Stopwatch? _uiSearchClock;
    private double _lastUiRefreshMilliseconds, _maximumUiRefreshMilliseconds;
    private double _endToEndMilliseconds;
    private double? _cancellationCompletionMilliseconds;
    private long _cancelRequestedTimestamp;
    private int _maximumPendingResults, _maximumPendingIssues;
    private int _resultBackpressureWaitCount, _issueBackpressureWaitCount, _backpressureUiWakeups;
    private double _resultBackpressureWaitMilliseconds, _issueBackpressureWaitMilliseconds;
    private DateTime? _lastCompletedUtc;

    [ObservableProperty] private bool hasDiagnostics;
    [ObservableProperty] private string diagnosticsText = "诊断仅含聚合统计；默认不保存搜索日志。";

    public SearchSummary? LastSearchSummary { get; private set; }

    public string ResourcePolicySummary =>
        $"资源护栏：单段/行/CSV 字段 {_resourceLimits.MaxSegmentCharacters:N0} 个 UTF-16 字符；每文件 {_resourceLimits.MaxMatchesPerFile:N0} 处；整轮 {_resourceLimits.MaxTotalMatches:N0} 处、{_resourceLimits.MaxMatchedFiles:N0} 个命中文件；结果文字每文件 {_resourceLimits.MaxResultCharactersPerFile:N0}、整轮 {_resourceLimits.MaxTotalResultCharacters:N0} 个 UTF-16 字符（含上下文，另有对象与解析器开销）。超限明确标注当前文件失败或整轮 Limited，不把部分文件当完整结果。诊断详情最多 {_resourceLimits.MaxIssueDetails:N0} 条，另附聚合说明。";

    private static Channel<T> CreateBoundedChannel<T>(int capacity) =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false
        });

    private void ResetSearchDiagnostics()
    {
        LastSearchSummary = null;
        HasDiagnostics = false;
        DiagnosticsText = "正在测量本次搜索；诊断仅含聚合统计，不自动保存日志。";
        _maximumPendingResults = _maximumPendingIssues = 0;
        _resultBackpressureWaitCount = _issueBackpressureWaitCount = _backpressureUiWakeups = 0;
        _resultBackpressureWaitMilliseconds = _issueBackpressureWaitMilliseconds = 0;
        _lastUiRefreshMilliseconds = _maximumUiRefreshMilliseconds = 0;
        _endToEndMilliseconds = 0;
        _cancellationCompletionMilliseconds = null;
        _cancelRequestedTimestamp = 0;
        _lastCompletedUtc = null;
        _uiSearchClock = Stopwatch.StartNew();
    }

    private void ObserveUiRefresh()
    {
        if (_uiSearchClock is null) return;
        var now = _uiSearchClock.Elapsed.TotalMilliseconds;
        _maximumUiRefreshMilliseconds = Math.Max(_maximumUiRefreshMilliseconds, now - _lastUiRefreshMilliseconds);
        _lastUiRefreshMilliseconds = now;
    }

    private void CompleteSearchDiagnostics(SearchSummary summary)
    {
        ObserveUiRefresh();
        _endToEndMilliseconds = _uiSearchClock?.Elapsed.TotalMilliseconds ?? 0;
        _uiSearchClock?.Stop();
        var cancelledAt = Interlocked.Read(ref _cancelRequestedTimestamp);
        _cancellationCompletionMilliseconds = cancelledAt == 0 ? null : Stopwatch.GetElapsedTime(cancelledAt).TotalMilliseconds;
        _lastCompletedUtc = DateTime.UtcNow;
        LastSearchSummary = summary;
        HasDiagnostics = true;
        var d = summary.Diagnostics;
        DiagnosticsText = $"本次 {_endToEndMilliseconds:N0} ms · 错误 {d.ErrorCount:N0} · 警告 {d.WarningCount:N0} · 省略详情 {d.SuppressedIssueCount:N0} · 最长界面刷新间隔 {_maximumUiRefreshMilliseconds:N0} ms" +
            (summary.Status == SearchStatus.Limited ? " · 已达到整轮资源上限，扫描未完成" : string.Empty);
    }

    /// <summary>User-requested aggregate export. Deliberately excludes all paths, terms,
    /// excerpts, exception messages and the raw limit reason, even for custom extractors.</summary>
    public string CreateDiagnosticSummary()
    {
        var summary = LastSearchSummary;
        return JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, CompletedUtc = _lastCompletedUtc,
            Status = summary?.Status.ToString() ?? "NotRun",
            ScannedFiles = summary?.Progress.ScannedFiles ?? 0,
            MatchedFiles = summary?.Progress.MatchedFiles ?? 0,
            MatchedOccurrences = summary?.Progress.Occurrences ?? 0,
            SkippedOrFailed = summary?.Progress.SkippedFiles ?? 0,
            DisplayedFiles = Results.Count, DisplayedOccurrences = _visibleOccurrences,
            ErrorCount = summary?.Diagnostics.ErrorCount ?? 0,
            WarningCount = summary?.Diagnostics.WarningCount ?? 0,
            SuppressedIssueCount = summary?.Diagnostics.SuppressedIssueCount ?? 0,
            SearchElapsedMilliseconds = summary?.Diagnostics.ElapsedMilliseconds ?? 0,
            EndToEndMilliseconds = _endToEndMilliseconds,
            CancellationCompletionMilliseconds = _cancellationCompletionMilliseconds,
            MaximumObservedUiRefreshIntervalMilliseconds = _maximumUiRefreshMilliseconds,
            PendingResultCapacity, PendingIssueCapacity,
            MaximumObservedPendingResults = _maximumPendingResults,
            MaximumObservedPendingIssues = _maximumPendingIssues,
            ResultBackpressureWaitCount = _resultBackpressureWaitCount,
            IssueBackpressureWaitCount = _issueBackpressureWaitCount,
            ResultBackpressureWaitMilliseconds = _resultBackpressureWaitMilliseconds,
            IssueBackpressureWaitMilliseconds = _issueBackpressureWaitMilliseconds,
            BackpressureUiWakeups = _backpressureUiWakeups,
            RetainedIssueCodeCounts = Issues.GroupBy(i => i.Code.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            ResourceLimits = _resourceLimits,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            Privacy = "Aggregate counts only; no paths, queries, excerpts or error messages. No automatic logging or telemetry."
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
