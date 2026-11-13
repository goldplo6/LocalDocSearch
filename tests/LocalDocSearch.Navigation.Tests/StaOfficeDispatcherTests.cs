using Xunit;

namespace LocalDocSearch.Navigation.Tests;

public sealed class StaOfficeDispatcherTests
{
    private static StaOfficeDispatcher Dispatcher(TimeSpan? timeout = null) =>
        new(timeout ?? TimeSpan.FromSeconds(3), requireSta: OperatingSystem.IsWindows());

    [Fact]
    public async Task Operations_run_on_a_dedicated_background_sta_thread_on_windows()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var result = await Dispatcher().ExecuteAsync(_ =>
            (Id: Environment.CurrentManagedThreadId, Background: Thread.CurrentThread.IsBackground,
                Apartment: Thread.CurrentThread.GetApartmentState()), CancellationToken.None);
        Assert.NotEqual(callerThread, result.Id);
        Assert.True(result.Background);
        if (OperatingSystem.IsWindows()) Assert.Equal(ApartmentState.STA, result.Apartment);
    }

    [Fact]
    public async Task Worker_exception_is_observed_and_does_not_escape_the_thread_or_hold_the_gate()
    {
        var dispatcher = Dispatcher();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.ExecuteAsync<int>(_ => throw new InvalidOperationException("COM fake failure"), CancellationToken.None));
        Assert.Equal("COM fake failure", error.Message);
        Assert.Equal(42, await dispatcher.ExecuteAsync(_ => 42, CancellationToken.None));
    }

    [Fact]
    public async Task Timed_out_call_keeps_gate_until_worker_finishes_without_terminating_it()
    {
        var dispatcher = Dispatcher(TimeSpan.FromMilliseconds(200));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var work = dispatcher.ExecuteAsync<int>(token =>
        {
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("test release missing");
                token.ThrowIfCancellationRequested();
                return 1;
            }
            finally { exited.TrySetResult(); }
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var error = await Assert.ThrowsAsync<OfficeTimeoutException>(() => work.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains("后台", error.Message);
            await Assert.ThrowsAsync<OfficeBusyException>(() => dispatcher.ExecuteAsync(_ => 2, CancellationToken.None));
            Assert.False(exited.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task Cancellation_stops_waiting_and_following_steps_but_does_not_kill_worker()
    {
        var dispatcher = Dispatcher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var followingStep = false;
        var work = dispatcher.ExecuteAsync(token =>
        {
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("test release missing");
                token.ThrowIfCancellationRequested();
                followingStep = true;
                return 1;
            }
            finally { exited.TrySetResult(); }
        }, stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(3)));
            await Assert.ThrowsAsync<OfficeBusyException>(() => dispatcher.ExecuteAsync(_ => 2, CancellationToken.None));
            Assert.False(exited.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.False(followingStep);
    }

    [Fact]
    public async Task Already_cancelled_dispatch_does_not_invoke_operation()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var invoked = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Dispatcher().ExecuteAsync(_ => { invoked = true; return 1; }, stop.Token));
        Assert.False(invoked);
    }
}
