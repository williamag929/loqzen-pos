using System;
using System.Collections.Generic;

namespace Core.Application.Security;

/// <summary>
/// Slows down PIN guessing on the terminal: after <see cref="MaxFailures"/> wrong PINs within
/// <see cref="Window"/> the PIN entry is locked for <see cref="Lockout"/>. One shared instance per app
/// (singleton), keyed by a scope string ("login" / "manager") so a locked login does not lock approvals.
/// </summary>
public interface IPinAttemptLimiter
{
    /// <summary>Seconds left before a new attempt is accepted; 0 = not locked.</summary>
    int SecondsLocked(string scope);

    /// <summary>Throws <see cref="PinLockedException"/> when locked.</summary>
    void EnsureNotLocked(string scope);

    void RegisterFailure(string scope);
    void RegisterSuccess(string scope);
}

public sealed class PinLockedException : InvalidOperationException
{
    public int SecondsLeft { get; }
    public PinLockedException(int secondsLeft)
        : base($"Demasiados intentos fallidos. Espere {secondsLeft} s.") => SecondsLeft = secondsLeft;
}

/// <summary>Default in-memory implementation (thread-safe, clock-injected).</summary>
public sealed class PinAttemptLimiter : IPinAttemptLimiter
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, (List<DateTimeOffset> Failures, DateTimeOffset? LockedUntil)> _state = new();

    public PinAttemptLimiter(TimeProvider clock) => _clock = clock;

    public int SecondsLocked(string scope)
    {
        lock (_gate)
        {
            if (!_state.TryGetValue(scope, out var s) || s.LockedUntil is not { } until) return 0;
            var left = until - _clock.GetUtcNow();
            return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalSeconds);
        }
    }

    public void EnsureNotLocked(string scope)
    {
        var left = SecondsLocked(scope);
        if (left > 0) throw new PinLockedException(left);
    }

    public void RegisterFailure(string scope)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!_state.TryGetValue(scope, out var s)) s = (new List<DateTimeOffset>(), null);
            s.Failures.RemoveAll(t => now - t > Window);
            s.Failures.Add(now);
            if (s.Failures.Count >= MaxFailures)
            {
                s.LockedUntil = now + Lockout;
                s.Failures.Clear();
            }
            _state[scope] = s;
        }
    }

    public void RegisterSuccess(string scope)
    {
        lock (_gate) _state.Remove(scope);
    }
}
