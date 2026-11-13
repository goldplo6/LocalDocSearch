using System.Diagnostics;

namespace LocalDocSearch.Navigation;

internal sealed class WindowsNavigationBackend : INavigationBackend
{
    private static readonly StaOfficeDispatcher OfficeDispatcher = new(TimeSpan.FromSeconds(12));
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(5);
    public bool IsWindows => OperatingSystem.IsWindows();

    public Task<bool> FileExistsAsync(string fullPath, CancellationToken cancellationToken) =>
        Task.Run(() => File.Exists(fullPath), cancellationToken).WaitAsync(ShellTimeout, cancellationToken);

    public async Task OpenShellAsync(string fullPath, CancellationToken cancellationToken)
    {
        if (!IsWindows) throw new PlatformNotSupportedException("文件打开仅支持 Windows。");
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No interpreter, arguments, verbs from input, URL, or arbitrary executable.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
                Verb = "open"
            });
            // ShellExecute may return no process for an already-running associated application.
        }, cancellationToken).WaitAsync(ShellTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<OfficeLocationEvidence> LocateOfficeAsync(OfficeTarget target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Office 自动化仅支持 Windows。");
        return OfficeDispatcher.ExecuteAsync(token =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Office 自动化仅支持 Windows。");
            return OfficeComNavigation.Locate(target, token);
        }, cancellationToken);
    }
}

/// <summary>At most one in-flight Office call across navigator instances. No GUI-thread COM.</summary>
internal sealed class StaOfficeDispatcher(TimeSpan timeout, bool requireSta = true)
{
    private int _running;

    internal async Task<T> ExecuteAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requireSta && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("STA Office 仅支持 Windows。");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new OfficeBusyException("上次 Office 调用仍在后台完成；本次未再次打开文件，也未结束任何 Office 进程。");

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new CancellationTokenSource();
        // Observe a late COM exception even when the caller already timed out or cancelled.
        _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var result = operation(stop.Token);
                    Volatile.Write(ref _running, 0);
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    Volatile.Write(ref _running, 0);
                    completion.TrySetCanceled();
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _running, 0);
                    completion.TrySetException(exception);
                }
                finally { stop.Dispose(); }
            }) { IsBackground = true, Name = "LocalDocSearch.OfficeNavigation.STA" };
            if (requireSta && OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            stop.Dispose();
            Volatile.Write(ref _running, 0);
            throw;
        }

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            StopFollowingSteps();
            throw new OfficeTimeoutException($"Office 响应超过 {timeout.TotalSeconds:g} 秒；已停止等待，调用可能仍在后台完成。未结束 Office；请检查窗口后重试。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopFollowingSteps();
            throw;
        }

        void StopFollowingSteps()
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* Worker completed while the waiting task was being cancelled. */ }
        }
    }
}
