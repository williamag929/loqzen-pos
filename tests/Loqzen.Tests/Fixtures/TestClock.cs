namespace Loqzen.Tests.Fixtures;

/// <summary>Manually-advanced clock for time-dependent services (comanda confirmation delay).</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
