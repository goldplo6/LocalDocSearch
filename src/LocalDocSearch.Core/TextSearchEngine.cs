using System.Buffers;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalDocSearch.Core;

/// <summary>
/// Read-only recursive search with bounded logical budgets. A file is published only after
/// extraction, formula qualification and metadata checks finish. Metadata checks are conservative
/// change detection, not a consistent snapshot: equal-size/equal-mtime changes can escape them.
/// Third-party extractors and their internal allocations are outside these logical budgets.
/// </summary>
public sealed partial class TextSearchEngine
{
    private readonly IReadOnlyDictionary<string, ITextExtractor> _extractors;
    private readonly SearchResourceLimits _limits;

    public TextSearchEngine(IEnumerable<ITextExtractor>? extractors = null, SearchResourceLimits? limits = null)
    {
        _limits = (limits ?? new()).Snapshot();
        var registry = new Dictionary<string, ITextExtractor>(StringComparer.OrdinalIgnoreCase);
        foreach (var extractor in extractors ?? new ITextExtractor[] { new PlainTextExtractor(), new CsvTextExtractor() })
            foreach (var extension in extractor.Extensions)
                if (!registry.TryAdd(extension, extractor))
                    throw new ArgumentException($"文件类型重复注册：{extension}", nameof(extractors));
        _extractors = registry;
    }

    public IReadOnlyCollection<string> SupportedExtensions => _extractors.Keys.ToArray();

    /// <summary>
    /// Searches sequentially by default. Experimental maxConcurrency 2 or 4 uses a bounded FIFO
    /// file window and preserves discovery-order publication. It may extract later files before an
    /// earlier result cancels the search; all started work is cancelled and joined before returning.
    /// Registered custom extractor instances are shared: opting in requires thread-safe independent
    /// concurrent enumerations, including any parser state, warning callbacks and external handles.
    /// </summary>
    public Task<SearchSummary> SearchAsync(
        SearchRequest request,
        IProgress<SearchProgress>? progress = null,
        IProgress<FileResult>? results = null,
        IProgress<SearchIssue>? issues = null,
        CancellationToken cancellationToken = default,
        SearchExecutionControl? executionControl = null,
        int maxConcurrency = 1)
    {
        if (maxConcurrency is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "并发数须为 1、2 或 4。");
        var query = Validate(request);
        // Do not pass the token to Task.Run: pre-cancellation still returns a Cancelled summary.
        // Opt-in concurrency invokes the SAME registered extractor concurrently; custom extractors
        // must support independent simultaneous enumerations. The default retains the original path.
        return Task.Run(() => maxConcurrency == 1
            ? SearchCoreAsync(query, progress, results, issues, cancellationToken, executionControl)
            : SearchConcurrentAsync(query, progress, results, issues, cancellationToken, executionControl, maxConcurrency));
    }

    private Query Validate(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limits = _limits.Snapshot();
        if (string.IsNullOrWhiteSpace(request.RootFolder))
            throw new ArgumentException("请选择有效的搜索根目录。", nameof(request));
        if (!Enum.IsDefined(request.MatchMode))
            throw new ArgumentException("请选择有效的搜索匹配模式。", nameof(request));
        Regex? regularExpression = null;
        SearchFormula.ExpressionSnapshot? expression = null;
        string[] terms;
        int[] termIndices;
        var comparison = request.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (request.MatchMode == SearchMatchMode.RegularExpression)
        {
            if (request.Terms is null || request.Terms.Count != 0 || request.Expression is not null || request.TermConnectors is not null)
                throw new ArgumentException("正则表达式搜索要求 Terms 为空，且 Expression 与 TermConnectors 为 null。", nameof(request));
            var error = RegularExpressionQuery.ValidateAndCreate(request.RegexPattern, request.MatchCase, limits, out regularExpression);
            if (error is not null) throw new ArgumentException(error, nameof(request));
            terms = [];
            termIndices = [];
        }
        else
        {
            if (request.RegexPattern is not null)
                throw new ArgumentException("字面量搜索中 RegexPattern 须为 null；请切换正则表达式模式。", nameof(request));
            if (request.Terms is null || request.Terms.Count == 0)
                throw new ArgumentException("请至少输入一个非空关键词；关键词列表中不得包含空项。", nameof(request));
            // Bound input before deduplication/copying; duplicate entries also consume the input budget.
            if (request.Terms.Count > limits.MaxTerms)
                throw new ArgumentException($"关键词数量须不超过 {limits.MaxTerms}（MaxTerms）。", nameof(request));
            var inputTerms = request.Terms.ToArray();
            if (inputTerms.Length == 0 || inputTerms.Length > limits.MaxTerms)
                throw new ArgumentException($"关键词数量须为 1 至 {limits.MaxTerms}（MaxTerms）。", nameof(request));
            if (inputTerms.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("请至少输入一个非空关键词；关键词列表中不得包含空项。", nameof(request));
            if (inputTerms.Any(term => term.Length > limits.MaxTermCharacters))
                throw new ArgumentException($"单个关键词的 UTF-16 长度须不超过 {limits.MaxTermCharacters}（MaxTermCharacters）。", nameof(request));
            if (!Enum.IsDefined(request.Operator))
                throw new ArgumentException("搜索逻辑必须为 AND 或 OR。", nameof(request));
            if (request.Expression is not null && request.TermConnectors is not null)
                throw new ArgumentException("Expression and TermConnectors cannot be supplied together.", nameof(request));
            // Validate and clone exactly once, before any path or filesystem access.
            expression = request.Expression is null
                ? SearchFormula.CreateSnapshot(inputTerms.Length, request.TermConnectors, request.Operator)!
                : SearchFormula.SnapshotExpression(request.Expression, inputTerms.Length);
            var comparer = request.MatchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            terms = inputTerms.Distinct(comparer).ToArray();
            // Match each distinct literal once, but retain every original expression position.
            var indices = new Dictionary<string, int>(comparer);
            for (var index = 0; index < terms.Length; index++) indices.Add(terms[index], index);
            termIndices = inputTerms.Select(term => indices[term]).ToArray();
        }
        if (!Enum.IsDefined(request.EncodingMode))
            throw new ArgumentException("请选择有效的文本编码模式。", nameof(request));
        if (request.ContextCharacters is < 0 or > 500)
            throw new ArgumentException("上下文字符数须为 0 至 500。", nameof(request));

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in request.Extensions ?? SupportedExtensions)
        {
            if (string.IsNullOrWhiteSpace(extension)) continue;
            var normalized = extension.Trim();
            if (!normalized.StartsWith('.')) normalized = "." + normalized;
            if (_extractors.ContainsKey(normalized)) extensions.Add(normalized);
        }
        if (extensions.Count == 0)
            throw new ArgumentException("请至少选择一种已注册的文件类型。", nameof(request));

        string root;
        try
        {
            root = Path.GetFullPath(request.RootFolder);
            if (!Directory.Exists(root))
                throw new ArgumentException("搜索根目录不存在或不可访问。", nameof(request));
            for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("搜索根目录及其祖先须为普通目录，请选择链接实际指向的目录。", nameof(request));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            throw new ArgumentException("搜索根目录不可访问：" + exception.Message, nameof(request), exception);
        }
        // Defer the immutable candidate set until a long segment actually needs it.
        // Lazy's default publication mode also shares one construction across file workers.
        var literalCandidates = terms.Length >= 32
            ? new Lazy<SearchValues<string>>(() => SearchValues.Create(terms, comparison)) : null;
        return new Query(root, terms, expression, termIndices, regularExpression, literalCandidates,
            extensions, comparison, request.ContextCharacters, request.EncodingMode, limits);
    }

    private async Task<SearchSummary> SearchCoreAsync(
        Query query, IProgress<SearchProgress>? progress, IProgress<FileResult>? results,
        IProgress<SearchIssue>? issues, CancellationToken cancellationToken, SearchExecutionControl? executionControl)
    {
        var state = new ScanState(query.Limits, progress, issues);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((query.Root, 0));
        try
        {
            await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
            state.ReportProgress(force: true);
            var firstFile = true;
            while (pending.TryPop(out var directory))
            {
                await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                FileAttributes directoryAttributes;
                try { directoryAttributes = File.GetAttributes(directory.Path); }
                catch (Exception exception) when (IsFileException(exception))
                {
                    RecordFailure(state, directory.Path, exception);
                    continue;
                }
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    state.ReportIssue(directory.Path, "已跳过目录链接或重解析点。", SearchIssueSeverity.Error);
                    continue;
                }

                await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                IEnumerator<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(directory.Path).GetEnumerator(); }
                catch (Exception exception) when (IsFileException(exception))
                {
                    RecordFailure(state, directory.Path, exception);
                    continue;
                }
                using (entries)
                {
                    while (true)
                    {
                        await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                        string path;
                        try
                        {
                            if (!entries.MoveNext()) break;
                            path = entries.Current;
                        }
                        catch (Exception exception) when (IsFileException(exception))
                        {
                            RecordFailure(state, directory.Path, exception);
                            break;
                        }

                        await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                        state.CurrentPath = path;
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); }
                        catch (Exception exception) when (IsFileException(exception))
                        {
                            state.SkippedFiles++;
                            RecordFailure(state, path, exception);
                            state.ReportProgress();
                            continue;
                        }
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            if (!isDirectory) state.SkippedFiles++;
                            state.ReportIssue(path, "已跳过链接或重解析点。", SearchIssueSeverity.Error);
                            state.ReportProgress();
                            continue;
                        }
                        if (isDirectory)
                        {
                            if (directory.Depth >= query.Limits.MaxDirectoryDepth)
                                throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxDirectoryDepth), query.Limits.MaxDirectoryDepth);
                            if (pending.Count >= query.Limits.MaxPendingDirectories)
                                throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxPendingDirectories), query.Limits.MaxPendingDirectories);
                            pending.Push((path, directory.Depth + 1));
                            continue;
                        }
                        if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal) ||
                            !query.Extensions.Contains(Path.GetExtension(path)))
                        {
                            state.SkippedFiles++;
                            state.ReportProgress();
                            continue;
                        }
                        if (state.ScannedFiles >= query.Limits.MaxScannedFiles)
                            throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxScannedFiles), query.Limits.MaxScannedFiles);

                        // Preserve immediate cancellation at the first eligible file and first published match.
                        state.ReportProgress(force: firstFile);
                        firstFile = false;
                        await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                        FileSearchResult? extracted;
                        try
                        {
                            extracted = await SearchFileAsync(path, query, state, cancellationToken, executionControl).ConfigureAwait(false);
                            await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (IsFileException(exception))
                        {
                            await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                            state.ScannedFiles++;
                            state.SkippedFiles++;
                            RecordFailure(state, path, exception);
                            state.ReportProgress();
                            continue;
                        }
                        state.ScannedFiles++;
                        state.SuccessfulFiles++;
                        var result = extracted?.File;
                        if (result is not null)
                        {
                            // Only a complete, formula-qualified file consumes global result budgets.
                            if (result.TotalOccurrences > query.Limits.MaxTotalMatches - state.Occurrences)
                                throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxTotalMatches), query.Limits.MaxTotalMatches);
                            if (extracted!.Value.ResultCharacters > query.Limits.MaxTotalResultCharacters - state.ResultCharacters)
                                throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxTotalResultCharacters), query.Limits.MaxTotalResultCharacters);
                            if (state.MatchedFiles >= query.Limits.MaxMatchedFiles)
                                throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxMatchedFiles), query.Limits.MaxMatchedFiles);
                            await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
                            state.MatchedFiles++;
                            state.Occurrences += result.TotalOccurrences;
                            state.ResultCharacters += extracted!.Value.ResultCharacters;
                            results?.Report(result);
                        }
                        state.ReportProgress(force: result is not null && state.MatchedFiles == 1);
                    }
                }
            }
            await CheckpointAsync(executionControl, cancellationToken).ConfigureAwait(false);
            var status = state.ErrorCount == 0 && state.WarningCount == 0 ? SearchStatus.Completed :
                state.SuccessfulFiles > 0 ? SearchStatus.Partial : SearchStatus.Failed;
            return state.Finish(status);
        }
        catch (QueryLimitReachedException exception)
        {
            if (cancellationToken.IsCancellationRequested) return state.Finish(SearchStatus.Cancelled);
            state.LimitReason = $"{exception.LimitName}={exception.Limit}";
            state.ReportIssue(state.CurrentPath ?? query.Root,
                $"搜索已因全局资源限额 {state.LimitReason} 停止；仅保留已完整发布的文件结果。",
                SearchIssueSeverity.Error, SearchIssueCode.ResourceLimit);
            return state.Finish(cancellationToken.IsCancellationRequested ? SearchStatus.Cancelled : SearchStatus.Limited);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return state.Finish(SearchStatus.Cancelled);
        }
    }

    private static ValueTask CheckpointAsync(SearchExecutionControl? executionControl, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return executionControl?.WaitIfPausedAsync(token) ?? ValueTask.CompletedTask;
    }

    private async Task<FileSearchResult?> SearchFileAsync(string path, Query query, ScanState state,
        CancellationToken token, SearchExecutionControl? executionControl)
    {
        await CheckpointAsync(executionControl, token).ConfigureAwait(false);
        var info = new FileInfo(path);
        info.Refresh();
        var modified = info.LastWriteTimeUtc;
        var length = info.Length;
        var found = new bool[query.Terms.Length];
        var matches = new List<MatchItem>();
        // Reuse scratch storage: most extracted lines have no match.
        var segmentMatches = new List<MatchItem>();
        var textBudget = new ResultTextBudget(query.Limits.MaxResultCharactersPerFile);
        // Start/stop only while handling matching, so extraction and I/O do not consume this budget.
        var regexTimer = query.RegularExpression is null ? null : new Stopwatch();
        var warnings = new List<string>();
        long warningCount = 0;
        var context = new ExtractionContext((message, code) =>
        {
            token.ThrowIfCancellationRequested();
            warningCount++;
            var retain = warnings.Count < query.Limits.MaxWarningsPerFile;
            if (retain) warnings.Add(message);
            state.ReportIssue(path, message, SearchIssueSeverity.Warning, code, allowDetail: retain);
        }, query.EncodingMode, query.Limits);
        var extractor = _extractors[Path.GetExtension(path)];
        await CheckpointAsync(executionControl, token).ConfigureAwait(false);
        await using (var segments = extractor.ExtractAsync(path, context, token).GetAsyncEnumerator(token))
        {
            while (true)
            {
                // Check both sides: MoveNextAsync may include an indivisible synchronous parser.
                await CheckpointAsync(executionControl, token).ConfigureAwait(false);
                var hasSegment = await segments.MoveNextAsync().ConfigureAwait(false);
                await CheckpointAsync(executionControl, token).ConfigureAwait(false);
                if (!hasSegment) break;
                var segment = segments.Current;
                var text = segment.Text;
                if (text.Length > query.Limits.MaxSegmentCharacters)
                    throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), query.Limits.MaxSegmentCharacters);
                segmentMatches.Clear();
                // IndexOfAny uses the same ordinal comparison as the per-term matcher. Nothing
                // can match before this offset; a negative result only skips this segment's loop,
                // never extraction, EOF validation, diagnostics or whole-file qualification.
                var literalStart = query.LiteralCandidates is not null && text.Length >= 256
                    ? text.AsSpan().IndexOfAny(query.LiteralCandidates.Value) : 0;
                if (query.RegularExpression is not null)
                {
                    // Keep a segment's regex work synchronous and budgeted. Pause is acknowledged
                    // at the surrounding checkpoints, outside the regex matching stopwatch.
                    MatchRegularExpression(segment, query, regexTimer!, matches.Count, segmentMatches, textBudget, token);
                }
                else for (var termIndex = 0; literalStart >= 0 && termIndex < query.Terms.Length; termIndex++)
                {
                    await CheckpointAsync(executionControl, token).ConfigureAwait(false);
                    var term = query.Terms[termIndex];
                    var start = literalStart;
                    var iterations = 0;
                    while (start <= text.Length - term.Length)
                    {
                        token.ThrowIfCancellationRequested();
                        // Dense literal hits stay on the synchronous fast path unless paused.
                        if ((iterations++ & 255) == 0 && executionControl?.IsPauseRequested == true)
                            await executionControl.WaitIfPausedAsync(token).ConfigureAwait(false);
                        var position = text.IndexOf(term, start, query.Comparison);
                        if (position < 0) break;
                        // Check before allocating any context substrings or a MatchItem.
                        if (segmentMatches.Count >= query.Limits.MaxMatchesPerFile - matches.Count)
                            throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxMatchesPerFile), query.Limits.MaxMatchesPerFile);
                        var end = position + term.Length;
                        var beforeStart = Math.Max(0, position - query.ContextCharacters);
                        var afterLength = Math.Min(query.ContextCharacters, text.Length - end);
                        textBudget.Reserve(position - beforeStart, term.Length, afterLength);
                        var location = segment.Source;
                        if (location.Kind == SourceKind.TextLine && location.Column != position + 1)
                            location = location with { Column = position + 1 };
                        segmentMatches.Add(new MatchItem(term, text[beforeStart..position], text[position..end],
                            text.Substring(end, afterLength), location, segment.Id, position));
                        found[termIndex] = true;
                        start = end;
                    }
                }
                await CheckpointAsync(executionControl, token).ConfigureAwait(false);
                if (segmentMatches.Count > 1) segmentMatches.Sort(static (left, right) =>
                {
                    var position = left.Offset.CompareTo(right.Offset);
                    return position != 0 ? position : StringComparer.Ordinal.Compare(left.Term, right.Term);
                });
                matches.AddRange(segmentMatches);
            }
        }
        await CheckpointAsync(executionControl, token).ConfigureAwait(false);
        info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != modified ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SourceFileChangedException();
        if (matches.Count == 0 || (query.Expression is not null &&
            !SearchFormula.EvaluateValidated(query.Expression, found, query.TermIndices))) return null;
        if (warningCount > warnings.Count)
            warnings.Add($"该文件另有 {warningCount - warnings.Count} 条警告详情已省略（MaxWarningsPerFile={query.Limits.MaxWarningsPerFile}；本条为额外聚合说明）。");
        var file = new FileResult(path, Path.GetExtension(path).ToLowerInvariant(), modified, matches.AsReadOnly())
        {
            ExtractionWarnings = warnings.AsReadOnly()
        };
        return new FileSearchResult(file, textBudget.Used);
    }

    private static void MatchRegularExpression(TextSegment segment, Query query, Stopwatch timer,
        int existingMatchCount, List<MatchItem> matches, ResultTextBudget textBudget, CancellationToken token)
    {
        timer.Start();
        try
        {
            CheckBudget();
            var match = query.RegularExpression!.Match(segment.Text);
            CheckBudget();
            while (match.Success)
            {
                // NextMatch handles progression after an empty match. Manually incrementing a
                // start offset would change standard .NET enumeration and can lose later hits.
                if (match.Length != 0)
                {
                    if (matches.Count >= query.Limits.MaxMatchesPerFile - existingMatchCount)
                        throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxMatchesPerFile), query.Limits.MaxMatchesPerFile);
                    var position = match.Index;
                    var end = position + match.Length;
                    var beforeStart = Math.Max(0, position - query.ContextCharacters);
                    var afterLength = Math.Min(query.ContextCharacters, segment.Text.Length - end);
                    textBudget.Reserve(position - beforeStart, match.Length, afterLength);
                    var location = segment.Source;
                    if (location.Kind == SourceKind.TextLine && location.Column != position + 1)
                        location = location with { Column = position + 1 };
                    matches.Add(new MatchItem(query.RegularExpression.ToString(), segment.Text[beforeStart..position],
                        match.Value, segment.Text.Substring(end, afterLength), location, segment.Id, position));
                }
                CheckBudget();
                match = match.NextMatch();
                CheckBudget();
            }
        }
        catch (RegexMatchTimeoutException)
        {
            token.ThrowIfCancellationRequested();
            // The framework exception contains Input and Pattern. Retain neither in the issue
            // nor in an inner exception that could later be displayed by a caller.
            throw new SearchResourceLimitException(nameof(SearchResourceLimits.RegexMatchTimeoutMilliseconds),
                query.Limits.RegexMatchTimeoutMilliseconds);
        }
        finally { timer.Stop(); }

        void CheckBudget()
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed.TotalMilliseconds >= query.Limits.MaxRegexFileMilliseconds)
                throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxRegexFileMilliseconds),
                    query.Limits.MaxRegexFileMilliseconds);
        }
    }

    private readonly record struct FileSearchResult(FileResult File, int ResultCharacters);

    private sealed class ResultTextBudget(int limit)
    {
        public int Used { get; private set; }

        public void Reserve(int beforeLength, int matchLength, int afterLength)
        {
            // Use long arithmetic before any context substring or regex Match.Value allocation.
            var next = (long)beforeLength + matchLength + afterLength;
            if (next > limit - Used)
                throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxResultCharactersPerFile), limit);
            Used += (int)next;
        }
    }

    private static bool IsFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or DecoderFallbackException;

    private static void RecordFailure(ScanState state, string path, Exception exception)
    {
        var code = exception switch
        {
            SearchResourceLimitException => SearchIssueCode.ResourceLimit,
            SourceFileChangedException => SearchIssueCode.FileChanged,
            DecoderFallbackException => SearchIssueCode.Encoding,
            UnauthorizedAccessException or SecurityException => SearchIssueCode.AccessDenied,
            ExtractionException or InvalidDataException => SearchIssueCode.InvalidData,
            _ => SearchIssueCode.Unknown
        };
        var reason = exception is DecoderFallbackException
            ? "文本解码失败：文件不符合所选编码模式（无 BOM 默认为严格 UTF-8，可选严格 GB18030；BOM 优先且须为有效 Unicode）。"
            : "读取失败：" + exception.Message;
        state.ReportIssue(path, reason, SearchIssueSeverity.Error, code);
    }

    private sealed class SourceFileChangedException()
        : IOException("文件在读取期间发生变化，已丢弃该文件的全部结果（长度及修改时间检查不构成一致快照）。");

    private sealed class QueryLimitReachedException(string limitName, int limit) : Exception
    {
        public string LimitName { get; } = limitName;
        public int Limit { get; } = limit;
    }

    private sealed record Query(string Root, string[] Terms, SearchFormula.ExpressionSnapshot? Expression,
        int[] TermIndices, Regex? RegularExpression, Lazy<SearchValues<string>>? LiteralCandidates,
        HashSet<string> Extensions, StringComparison Comparison, int ContextCharacters,
        TextEncodingMode EncodingMode, SearchResourceLimits Limits);

    private sealed class ScanState(SearchResourceLimits limits, IProgress<SearchProgress>? progress, IProgress<SearchIssue>? issues)
    {
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private long _lastProgressMilliseconds = -50;
        private int _issueDetails;
        public int ScannedFiles;
        public int SuccessfulFiles;
        public int MatchedFiles;
        public int Occurrences;
        public int ResultCharacters;
        public int SkippedFiles;
        public long ErrorCount;
        public long WarningCount;
        public long SuppressedIssueCount;
        public string? LimitReason;
        public string? CurrentPath;
        public SearchProgress Snapshot() => new(ScannedFiles, MatchedFiles, Occurrences, SkippedFiles, CurrentPath);

        public void ReportProgress(bool force = false)
        {
            if (!force && _timer.ElapsedMilliseconds - _lastProgressMilliseconds < 50) return;
            _lastProgressMilliseconds = _timer.ElapsedMilliseconds;
            progress?.Report(Snapshot());
        }

        public void ReportIssue(string path, string message, SearchIssueSeverity severity,
            SearchIssueCode code = SearchIssueCode.Unknown, bool allowDetail = true)
        {
            if (severity == SearchIssueSeverity.Error) ErrorCount++; else WarningCount++;
            if (!allowDetail || _issueDetails >= limits.MaxIssueDetails)
            {
                SuppressedIssueCount++;
                return;
            }
            _issueDetails++;
            issues?.Report(new SearchIssue(path, message, severity, code));
        }

        public SearchSummary Finish(SearchStatus status)
        {
            if (SuppressedIssueCount > 0)
                issues?.Report(new SearchIssue("",
                    $"另有 {SuppressedIssueCount} 条问题详情已省略（全局 MaxIssueDetails={limits.MaxIssueDetails}，每文件 MaxWarningsPerFile={limits.MaxWarningsPerFile}）；错误总数 {ErrorCount}，警告总数 {WarningCount}。本条为限额外唯一聚合说明，不计入诊断问题总数。",
                    SearchIssueSeverity.Warning, SearchIssueCode.ResourceLimit));
            ReportProgress(force: true);
            _timer.Stop();
            return new SearchSummary(status, Snapshot())
            {
                Diagnostics = new SearchDiagnostics
                {
                    ErrorCount = ErrorCount, WarningCount = WarningCount, SuppressedIssueCount = SuppressedIssueCount,
                    ElapsedMilliseconds = _timer.Elapsed.TotalMilliseconds, LimitReason = LimitReason
                }
            };
        }
    }
}
