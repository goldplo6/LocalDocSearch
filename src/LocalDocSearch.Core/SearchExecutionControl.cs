namespace LocalDocSearch.Core;

/// <summary>
/// Cooperative, reusable pause control. A pause request is acknowledged only while a caller
/// is waiting at a checkpoint; synchronous work already in progress finishes first.
/// Cancellation belongs to each waiter and never resumes other callers.
/// </summary>
public sealed class SearchExecutionControl
{
    private readonly object _sync = new();
    private PauseRequest? _pause;
    private readonly HashSet<Participant> _participants = [];
    private readonly Participant? _participant;

    public SearchExecutionControl() { }
    private SearchExecutionControl(Participant participant) => _participant = participant;

    // Register before scheduling work: a not-yet-started worker is active, not paused.
    internal Participant RegisterParticipant()
    {
        lock (_sync)
        {
            var participant = new Participant(this);
            _participants.Add(participant);
            return participant;
        }
    }

    public bool IsPauseRequested => _participant?.Owner.IsPauseRequested ?? (Volatile.Read(ref _pause) is not null);

    /// <summary>
    /// Public independent waiters retain their any-waiter semantics. During a scoped concurrent
    /// search, every active participant must acknowledge the current generation; idle coordinators
    /// do not prevent acknowledgement and an in-progress extractor never acknowledges early.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            if (_participant is { } scope) return scope.Owner.IsPaused;
            lock (_sync)
            {
                if (_pause is not { } pause) return false;
                if (_participants.Count == 0) return pause.Waiters > 0;
                var active = false;
                foreach (var participant in _participants)
                {
                    if (participant.Idle) continue;
                    active = true;
                    if (participant.Acknowledged != pause) return false;
                }
                return active;
            }
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_pause is null) Volatile.Write(ref _pause, new PauseRequest());
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            var pause = _pause;
            if (pause is null) return;
            Volatile.Write(ref _pause, null);
            // Continuations run outside this lock, and recheck the current request before exiting.
            pause.Resumed.TrySetResult();
        }
    }

    public ValueTask WaitIfPausedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_participant is { } participant)
            return participant.Owner.WaitParticipantAsync(participant, cancellationToken);
        // The usual running checkpoint needs neither a lock nor a Task allocation.
        if (Volatile.Read(ref _pause) is null) return ValueTask.CompletedTask;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pause = _pause;
            if (pause is null) return ValueTask.CompletedTask;
            pause.Waiters++;
            return new ValueTask(WaitForResumeAsync(pause, cancellationToken));
        }
    }

    private async Task WaitForResumeAsync(PauseRequest pause, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                // WaitAsync cancels only this wait; it never cancels the shared resume signal.
                await pause.Resumed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) pause.Waiters--;
            }

            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Resume followed immediately by Pause must not let an old signal release
                // a caller through the newer pause. Acknowledgement is per request generation.
                var current = _pause;
                if (current is null) return;
                pause = current;
                pause.Waiters++;
            }
        }
    }

    private ValueTask WaitParticipantAsync(Participant participant, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _pause) is null) return ValueTask.CompletedTask;
        return new ValueTask(WaitParticipantCoreAsync(participant, token));
    }

    private async Task WaitParticipantCoreAsync(Participant participant, CancellationToken token)
    {
        while (true)
        {
            PauseRequest pause;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (_pause is not { } current) return;
                pause = current;
                participant.Acknowledged = pause;
            }
            try { await pause.Resumed.Task.WaitAsync(token).ConfigureAwait(false); }
            finally
            {
                lock (_sync)
                    if (participant.Acknowledged == pause) participant.Acknowledged = null;
            }
        }
    }

    internal sealed class Participant : IDisposable
    {
        internal readonly SearchExecutionControl Owner;
        internal bool Idle;
        private PauseRequest? _acknowledged;
        private bool _disposed;
        // Accessed only with Owner._sync held.
        internal PauseRequest? Acknowledged { get => _acknowledged; set => _acknowledged = value; }
        internal SearchExecutionControl Control { get; }

        internal Participant(SearchExecutionControl owner)
        {
            Owner = owner;
            Control = new SearchExecutionControl(this);
        }

        internal void SetIdle(bool idle)
        {
            lock (Owner._sync)
            {
                Idle = idle;
                _acknowledged = null;
            }
        }

        public void Dispose()
        {
            lock (Owner._sync)
            {
                if (_disposed) return;
                _disposed = true;
                Owner._participants.Remove(this);
            }
        }
    }

    internal sealed class PauseRequest
    {
        public readonly TaskCompletionSource Resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters;
    }
}
