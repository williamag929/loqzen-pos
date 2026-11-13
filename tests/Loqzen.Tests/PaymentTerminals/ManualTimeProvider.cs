namespace Loqzen.Tests.PaymentTerminals;

/// <summary>
/// Controllable <see cref="TimeProvider"/> whose timers fire from <see cref="Advance"/> instead of wall-clock time.
/// The payment-terminal code waits with <c>Task.Delay(ts, clock, ct)</c> and <c>new CancellationTokenSource(ts, clock)</c>,
/// both of which go through <see cref="CreateTimer"/>, so a 90 s charge timeout elapses in a test without sleeping.
/// (Fixtures/TestClock only overrides GetUtcNow: its timers would still run on real time.)
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Timers currently armed (not disposed, due time set).</summary>
    public int ActiveTimers
    {
        get { lock (_lock) return _timers.Count(t => t.Due != null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that becomes due (in due order) along the way.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due != null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next == null) break;
                _now = next.Due!.Value;
                if (next.Period > TimeSpan.Zero) next.Due = _now + next.Period;
                else next.Due = null;
            }
            next.Fire();
        }
        _now = target;
    }

    private void Register(ManualTimer t) { lock (_lock) _timers.Add(t); }
    private void Unregister(ManualTimer t) { lock (_lock) _timers.Remove(t); }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        public DateTimeOffset? Due;
        public TimeSpan Period;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            owner.Register(this);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }
            return true;
        }

        public void Fire() => _callback(_state);

        public void Dispose() => _owner.Unregister(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
