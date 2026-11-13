using Core.Application.Security;
using FluentAssertions;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Security;

/// <summary>5 failures within 1 minute lock a scope for 30 s; scopes are independent; success resets.</summary>
public class PinAttemptLimiterTests
{
    private readonly TestClock _clock = new();
    private readonly PinAttemptLimiter _limiter;

    public PinAttemptLimiterTests() => _limiter = new PinAttemptLimiter(_clock);

    private void Fail(string scope, int times)
    {
        for (var i = 0; i < times; i++) _limiter.RegisterFailure(scope);
    }

    [Fact]
    public void Constants_MatchPolicy()
    {
        PinAttemptLimiter.MaxFailures.Should().Be(5);
        PinAttemptLimiter.Window.Should().Be(TimeSpan.FromMinutes(1));
        PinAttemptLimiter.Lockout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void FourFailures_NotLocked()
    {
        Fail("login", 4);

        _limiter.SecondsLocked("login").Should().Be(0);
        var act = () => _limiter.EnsureNotLocked("login");
        act.Should().NotThrow();
    }

    [Fact]
    public void FifthFailure_LocksFor30Seconds()
    {
        Fail("login", 5);

        _limiter.SecondsLocked("login").Should().Be(30);
        var act = () => _limiter.EnsureNotLocked("login");
        act.Should().Throw<PinLockedException>()
            .Which.SecondsLeft.Should().Be(30);
    }

    [Fact]
    public void LockedException_IsInvalidOperation_WithSpanishMessage()
    {
        Fail("login", 5);
        var ex = Record.Exception(() => _limiter.EnsureNotLocked("login"));
        ex.Should().BeOfType<PinLockedException>().And.BeAssignableTo<InvalidOperationException>();
        ex!.Message.Should().Contain("30").And.Contain("intentos");
    }

    [Fact]
    public void Locked_CountsDownWithClock()
    {
        Fail("login", 5);

        _clock.Advance(TimeSpan.FromSeconds(15));
        _limiter.SecondsLocked("login").Should().Be(15);
        var act = () => _limiter.EnsureNotLocked("login");
        act.Should().Throw<PinLockedException>().Which.SecondsLeft.Should().Be(15);

        _clock.Advance(TimeSpan.FromSeconds(14.5));
        _limiter.SecondsLocked("login").Should().Be(1, "rounded up: half a second still blocks");
    }

    [Fact]
    public void After30Seconds_Unlocked()
    {
        Fail("login", 5);
        _clock.Advance(TimeSpan.FromSeconds(30));

        _limiter.SecondsLocked("login").Should().Be(0);
        var act = () => _limiter.EnsureNotLocked("login");
        act.Should().NotThrow();
    }

    [Fact]
    public void FailuresOlderThanOneMinute_DoNotCount()
    {
        Fail("login", 4);
        _clock.Advance(TimeSpan.FromSeconds(61));

        _limiter.RegisterFailure("login");

        _limiter.SecondsLocked("login").Should().Be(0, "the four old failures expired; this is the first in the window");

        Fail("login", 3);
        _limiter.SecondsLocked("login").Should().Be(0);
        _limiter.RegisterFailure("login");
        _limiter.SecondsLocked("login").Should().Be(30, "fifth failure inside the window");
    }

    [Fact]
    public void FailuresSpreadInsideTheWindow_StillLock()
    {
        for (var i = 0; i < 4; i++)
        {
            _limiter.RegisterFailure("login");
            _clock.Advance(TimeSpan.FromSeconds(10));
        }
        _limiter.RegisterFailure("login"); // 40 s after the first one

        _limiter.SecondsLocked("login").Should().Be(30);
    }

    [Fact]
    public void Success_ResetsFailures()
    {
        Fail("login", 4);
        _limiter.RegisterSuccess("login");

        Fail("login", 4);
        _limiter.SecondsLocked("login").Should().Be(0, "the counter restarted after the success");

        _limiter.RegisterFailure("login");
        _limiter.SecondsLocked("login").Should().Be(30);
    }

    [Fact]
    public void Success_ClearsAnActiveLock()
    {
        Fail("login", 5);
        _limiter.RegisterSuccess("login");

        _limiter.SecondsLocked("login").Should().Be(0);
    }

    [Fact]
    public void Scopes_AreIndependent()
    {
        Fail("login", 5);

        _limiter.SecondsLocked("login").Should().Be(30);
        _limiter.SecondsLocked("manager").Should().Be(0);
        var act = () => _limiter.EnsureNotLocked("manager");
        act.Should().NotThrow();

        Fail("manager", 4);
        _limiter.SecondsLocked("manager").Should().Be(0);
        _limiter.RegisterSuccess("manager");
        _limiter.SecondsLocked("login").Should().Be(30, "a success on another scope does not unlock login");
    }

    [Fact]
    public void UnknownScope_IsNotLocked()
    {
        _limiter.SecondsLocked("nunca-usado").Should().Be(0);
        var act = () => _limiter.EnsureNotLocked("nunca-usado");
        act.Should().NotThrow();
    }

    [Fact]
    public void AfterLockExpires_FailureCounterStartsFresh()
    {
        Fail("login", 5);
        _clock.Advance(TimeSpan.FromSeconds(30));

        Fail("login", 4);
        _limiter.SecondsLocked("login").Should().Be(0, "the failures that caused the lock were cleared");
        _limiter.RegisterFailure("login");
        _limiter.SecondsLocked("login").Should().Be(30);
    }
}
