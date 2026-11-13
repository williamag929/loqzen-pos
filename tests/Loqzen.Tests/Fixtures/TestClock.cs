namespace Loqzen.Tests.Fixtures;

/// <summary>Manually-advanced clock for time-dependent services (comanda confirmation delay).</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;
    private readonly TimeZoneInfo? _zone;

    /// <param name="zone">Local time zone reported by <see cref="LocalTimeZone"/> (day consolidation). Defaults to the machine's.</param>
    public TestClock(DateTimeOffset? start = null, TimeZoneInfo? zone = null)
    {
        _now = start ?? new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        _zone = zone;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => _zone ?? base.LocalTimeZone;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void Set(DateTimeOffset now) => _now = now;
}
