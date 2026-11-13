using System.Runtime.ExceptionServices;

namespace LocalDocSearch.Core;

public sealed partial class TextSearchEngine
{
    // Experimental opt-in path. W bounds running + completed-but-unpublished files together.
    // The coordinator alone enumerates, replays diagnostics and owns all global budgets.
    private async Task<SearchSummary> SearchConcurrentAsync(Query query, IProgress<SearchProgress>? progress,
        IProgress<FileResult>? results, IProgress<SearchIssue>? issues, CancellationToken cancellationToken,
        SearchExecutionControl? executionControl, int width)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var coordinator = executionControl?.RegisterParticipant();
        var control = coordinator?.Control;
        var token = stop.Token;
        var state = new ScanState(query.Limits, progress, issues);
        var window = new Queue<Task<CompletedFile>>();
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await CheckpointAsync(control, token).ConfigureAwait(false);
            state.ReportProgress(force: true);
            var firstFile = true;
            foreach (var entry in Discover(query))
            {
                await CheckpointAsync(control, token).ConfigureAwait(false);
                if (!entry.Eligible)
                {
                    // Directory/skip/error events retain their position relative to file diagnostics.
                    while (window.Count > 0) await PublishOldestAsync().ConfigureAwait(false);
                    await CheckpointAsync(control, token).ConfigureAwait(false);
                    if (!entry.SetCurrentPath && entry.Failure is null && entry.Message is null) continue;
                    if (entry.SetCurrentPath) state.CurrentPath = entry.Path;
                    if (entry.Limit is not null) throw entry.Limit;
                    state.SkippedFiles += entry.Skipped;
                    if (entry.Failure is not null) RecordFailure(state, entry.Path, entry.Failure);
                    if (entry.Message is not null)
                        state.ReportIssue(entry.Path, entry.Message, SearchIssueSeverity.Error);
                    state.ReportProgress();
                    continue;
                }
                if (state.ScannedFiles + window.Count >= query.Limits.MaxScannedFiles)
                {
                    while (window.Count > 0) await PublishOldestAsync().ConfigureAwait(false);
                    state.CurrentPath = entry.Path;
                    throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxScannedFiles), query.Limits.MaxScannedFiles);
                }
                state.CurrentPath = entry.Path;
                state.ReportProgress(force: firstFile);
                firstFile = false;
                // A synchronous first-file progress callback can cancel before ANY extractor starts.
                await CheckpointAsync(control, token).ConfigureAwait(false);
                var worker = executionControl?.RegisterParticipant();
                Task<CompletedFile> task;
                try
                {
                    task = Task.Run(async () =>
                    {
                        using (worker)
                        {
                            var collected = new List<SearchIssue>();
                            var local = new ScanState(query.Limits, null, new DirectProgress<SearchIssue>(collected.Add));
                            try
                            {
                                var extracted = await SearchFileAsync(entry.Path, query, local, token, worker?.Control).ConfigureAwait(false);
                                return new CompletedFile(entry.Path, extracted, collected, local.WarningCount, null);
                            }
                            catch (Exception exception) when (IsFileException(exception))
                            {
                                return new CompletedFile(entry.Path, null, collected, local.WarningCount, exception);
                            }
                            catch (Exception exception)
                            {
                                if (exception is not OperationCanceledException || !token.IsCancellationRequested)
                                {
                                    fatal.TrySetResult(exception);
                                    stop.Cancel();
                                }
                                throw;
                            }
                        }
                    });
                }
                catch { worker?.Dispose(); throw; }
                window.Enqueue(task);
                if (window.Count == width) await PublishOldestAsync().ConfigureAwait(false);
            }
            while (window.Count > 0) await PublishOldestAsync().ConfigureAwait(false);
            await CheckpointAsync(control, token).ConfigureAwait(false);
            return state.Finish(state.ErrorCount == 0 && state.WarningCount == 0 ? SearchStatus.Completed :
                state.SuccessfulFiles > 0 ? SearchStatus.Partial : SearchStatus.Failed);
        }
        catch (QueryLimitReachedException exception)
        {
            // Cleanup is awaited BEFORE final progress/summary callbacks are delivered.
            await StopWorkersAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return state.Finish(SearchStatus.Cancelled);
            state.LimitReason = $"{exception.LimitName}={exception.Limit}";
            state.ReportIssue(state.CurrentPath ?? query.Root,
                $"搜索已因全局资源限额 {state.LimitReason} 停止；仅保留已完整发布的文件结果。",
                SearchIssueSeverity.Error, SearchIssueCode.ResourceLimit);
            return state.Finish(cancellationToken.IsCancellationRequested ? SearchStatus.Cancelled : SearchStatus.Limited);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopWorkersAsync().ConfigureAwait(false);
            return state.Finish(SearchStatus.Cancelled);
        }
        catch (OperationCanceledException) when (fatal.Task.IsCompletedSuccessfully)
        {
            ExceptionDispatchInfo.Capture(await fatal.Task.ConfigureAwait(false)).Throw();
            throw;
        }
        finally { await StopWorkersAsync().ConfigureAwait(false); }

        async Task StopWorkersAsync()
        {
            stop.Cancel();
            coordinator?.SetIdle(true);
            try { await Task.WhenAll(window).ConfigureAwait(false); }
            catch { /* Observe every started task; preserve the original failure/cancellation. */ }
            finally { coordinator?.SetIdle(false); }
        }

        async Task PublishOldestAsync()
        {
            await CheckpointAsync(control, token).ConfigureAwait(false);
            // Keep the head in the window until observed, including on failure.
            var head = window.Peek();
            CompletedFile file;
            coordinator?.SetIdle(true);
            try
            {
                var finished = await Task.WhenAny(head, fatal.Task).ConfigureAwait(false);
                if (finished == fatal.Task)
                    ExceptionDispatchInfo.Capture(await fatal.Task.ConfigureAwait(false)).Throw();
                file = await head.ConfigureAwait(false);
            }
            finally { coordinator?.SetIdle(false); }
            await CheckpointAsync(control, token).ConfigureAwait(false);
            _ = window.Dequeue();
            state.CurrentPath = file.Path;
            foreach (var issue in file.Issues)
            {
                await CheckpointAsync(control, token).ConfigureAwait(false);
                state.ReportIssue(issue.Path, issue.Message, issue.Severity, issue.Code);
            }
            var omittedWarnings = file.WarningCount - file.Issues.Count;
            state.WarningCount += omittedWarnings;
            state.SuppressedIssueCount += omittedWarnings;
            await CheckpointAsync(control, token).ConfigureAwait(false);
            state.ScannedFiles++;
            if (file.Failure is not null)
            {
                state.SkippedFiles++;
                RecordFailure(state, file.Path, file.Failure);
                state.ReportProgress();
                return;
            }
            state.SuccessfulFiles++;
            if (file.Result is { } extracted)
            {
                var result = extracted.File;
                if (result.TotalOccurrences > query.Limits.MaxTotalMatches - state.Occurrences)
                    throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxTotalMatches), query.Limits.MaxTotalMatches);
                if (extracted.ResultCharacters > query.Limits.MaxTotalResultCharacters - state.ResultCharacters)
                    throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxTotalResultCharacters), query.Limits.MaxTotalResultCharacters);
                if (state.MatchedFiles >= query.Limits.MaxMatchedFiles)
                    throw new QueryLimitReachedException(nameof(SearchResourceLimits.MaxMatchedFiles), query.Limits.MaxMatchedFiles);
                await CheckpointAsync(control, token).ConfigureAwait(false);
                state.MatchedFiles++;
                state.Occurrences += result.TotalOccurrences;
                state.ResultCharacters += extracted.ResultCharacters;
                results?.Report(result);
            }
            await CheckpointAsync(control, token).ConfigureAwait(false);
            state.ReportProgress(force: file.Result is not null && state.MatchedFiles == 1);
        }
    }

    private sealed record CompletedFile(string Path, FileSearchResult? Result,
        List<SearchIssue> Issues, long WarningCount, Exception? Failure);
    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record Discovery(string Path, bool Eligible = false, int Skipped = 0,
        Exception? Failure = null, string? Message = null, QueryLimitReachedException? Limit = null,
        bool SetCurrentPath = true);

    // Same native enumeration and LIFO pending-directory order as the sequential baseline.
    // Yield directory markers too, so the coordinator checkpoints between filesystem operations.
    private static IEnumerable<Discovery> Discover(Query query)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((query.Root, 0));
        while (pending.TryPop(out var directory))
        {
            yield return new Discovery(directory.Path, SetCurrentPath: false);
            var attributes = default(FileAttributes);
            Exception? failure = null;
            try { attributes = File.GetAttributes(directory.Path); }
            catch (Exception exception) when (IsFileException(exception)) { failure = exception; }
            if (failure is not null) { yield return new Discovery(directory.Path, Failure: failure, SetCurrentPath: false); continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                yield return new Discovery(directory.Path, Message: "已跳过目录链接或重解析点。", SetCurrentPath: false);
                continue;
            }
            IEnumerator<string>? entries = null;
            try { entries = Directory.EnumerateFileSystemEntries(directory.Path).GetEnumerator(); }
            catch (Exception exception) when (IsFileException(exception)) { failure = exception; }
            if (failure is not null) { yield return new Discovery(directory.Path, Failure: failure, SetCurrentPath: false); continue; }
            using (entries)
            {
                while (true)
                {
                    string? path = null;
                    try { if (entries!.MoveNext()) path = entries.Current; }
                    catch (Exception exception) when (IsFileException(exception)) { failure = exception; }
                    if (failure is not null) { yield return new Discovery(directory.Path, Failure: failure, SetCurrentPath: false); break; }
                    if (path is null) break;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception exception) when (IsFileException(exception)) { failure = exception; }
                    if (failure is not null)
                    {
                        yield return new Discovery(path, Skipped: 1, Failure: failure);
                        failure = null;
                        continue;
                    }
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        yield return new Discovery(path, Skipped: isDirectory ? 0 : 1, Message: "已跳过链接或重解析点。");
                    else if (isDirectory)
                    {
                        QueryLimitReachedException? limit = null;
                        if (directory.Depth >= query.Limits.MaxDirectoryDepth)
                            limit = new(nameof(SearchResourceLimits.MaxDirectoryDepth), query.Limits.MaxDirectoryDepth);
                        else if (pending.Count >= query.Limits.MaxPendingDirectories)
                            limit = new(nameof(SearchResourceLimits.MaxPendingDirectories), query.Limits.MaxPendingDirectories);
                        if (limit is null) pending.Push((path, directory.Depth + 1));
                        yield return new Discovery(path, Limit: limit);
                    }
                    else if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal) ||
                        !query.Extensions.Contains(Path.GetExtension(path)))
                        yield return new Discovery(path, Skipped: 1);
                    else yield return new Discovery(path, Eligible: true);
                }
            }
        }
    }
}
