using Core.Application.DTOs;
using Core.Application.Security;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>
/// PIN brute-force protection wired into UserService (scope "login") and ManagerAuthorizationService
/// (scope "manager") through one shared <see cref="PinAttemptLimiter"/>.
/// </summary>
public class PinLockoutTests : IDisposable
{
    private const string ManagerPin = "5555";
    private const string CashierPin = "1111";
    private const string WrongPin = "0000";

    private readonly PosDbContext _db = TestDbContextFactory.Create();
    private readonly TestClock _clock = new();
    private readonly PinAttemptLimiter _limiter;
    private readonly UserService _users;
    private readonly ManagerAuthorizationService _auth;
    private readonly User _manager;
    private readonly User _cashier;

    public PinLockoutTests()
    {
        _manager = new User { Name = "Gerente", PinHash = PinHasher.Hash(ManagerPin), Role = UserRole.Manager };
        _cashier = new User { Name = "Cajero", PinHash = PinHasher.Hash(CashierPin), Role = UserRole.Cashier };
        _db.Users.AddRange(_manager, _cashier);
        _db.SaveChanges();

        _limiter = new PinAttemptLimiter(_clock);
        _users = new UserService(_db, _clock, _limiter);
        _auth = new ManagerAuthorizationService(_db, new AuthorizationSettings(), _clock, _limiter);
    }

    public void Dispose() => _db.Dispose();

    private static AuthorizationRequest Request() => new(AuthorizedAction.ReturnItem, "Devolución", Amount: 1000m);

    private async Task FailLoginAsync(int times, string pin = WrongPin)
    {
        for (var i = 0; i < times; i++)
            (await _users.AuthenticateByPinAsync(pin)).Should().BeNull();
    }

    private async Task FailManagerAsync(int times, string pin = WrongPin)
    {
        for (var i = 0; i < times; i++)
        {
            var act = () => _auth.AuthorizeWithPinAsync(Request(), pin);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    // ---- UserService: login ----

    [Fact]
    public async Task Login_FourWrongPins_CorrectPinStillWorks()
    {
        await FailLoginAsync(4);

        var dto = await _users.AuthenticateByPinAsync(CashierPin);

        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Cajero");
    }

    [Fact]
    public async Task Login_FiveWrongPins_SixthCallThrowsLocked_EvenWithCorrectPin_AndNobodyIsLoggedIn()
    {
        await FailLoginAsync(5);

        var act = () => _users.AuthenticateByPinAsync(CashierPin);

        var ex = await act.Should().ThrowAsync<PinLockedException>();
        ex.Which.SecondsLeft.Should().Be(30);
        _limiter.SecondsLocked("login").Should().Be(30);
        (await _db.Users.AsNoTracking().ToListAsync()).Should().OnlyContain(u => u.LastLoginAt == null, "nothing was logged in");
    }

    [Fact]
    public async Task Login_After30Seconds_CorrectPinWorks_AndLockIsCleared()
    {
        await FailLoginAsync(5);
        _clock.Advance(TimeSpan.FromSeconds(29));
        var stillLocked = () => _users.AuthenticateByPinAsync(CashierPin);
        await stillLocked.Should().ThrowAsync<PinLockedException>();

        _clock.Advance(TimeSpan.FromSeconds(1));
        var dto = await _users.AuthenticateByPinAsync(CashierPin);

        dto.Should().NotBeNull();
        dto!.LastLoginAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        _limiter.SecondsLocked("login").Should().Be(0);
    }

    [Fact]
    public async Task Login_MalformedPins_CountAsFailures()
    {
        (await _users.AuthenticateByPinAsync("12")).Should().BeNull();
        (await _users.AuthenticateByPinAsync("abcd")).Should().BeNull();
        (await _users.AuthenticateByPinAsync("")).Should().BeNull();
        (await _users.AuthenticateByPinAsync(null!)).Should().BeNull();
        (await _users.AuthenticateByPinAsync("1234567")).Should().BeNull();

        _limiter.SecondsLocked("login").Should().Be(30);
        var act = () => _users.AuthenticateByPinAsync(CashierPin);
        await act.Should().ThrowAsync<PinLockedException>();
    }

    [Fact]
    public async Task Login_Success_ResetsFailureCounter()
    {
        await FailLoginAsync(4);
        (await _users.AuthenticateByPinAsync(ManagerPin)).Should().NotBeNull();

        await FailLoginAsync(4);

        _limiter.SecondsLocked("login").Should().Be(0, "the counter restarted after the successful login");
        (await _users.AuthenticateByPinAsync(CashierPin)).Should().NotBeNull();
    }

    [Fact]
    public async Task Login_Locked_DoesNotBlockManagerApproval_OnSharedLimiter()
    {
        await FailLoginAsync(5);
        _limiter.SecondsLocked("login").Should().Be(30);

        var dto = await _auth.AuthorizeWithPinAsync(Request(), ManagerPin);

        dto.ManagerName.Should().Be("Gerente");
        (await _db.ManagerOverrides.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ManagerLocked_DoesNotBlockLogin_OnSharedLimiter()
    {
        await FailManagerAsync(5);
        _limiter.SecondsLocked("manager").Should().Be(30);

        var dto = await _users.AuthenticateByPinAsync(CashierPin);

        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Cajero");
    }

    [Fact]
    public async Task Login_ServiceWithoutLimiter_NeverLocks()
    {
        var unprotected = new UserService(_db, _clock);
        for (var i = 0; i < 10; i++)
            (await unprotected.AuthenticateByPinAsync(WrongPin)).Should().BeNull();

        var dto = await unprotected.AuthenticateByPinAsync(CashierPin);

        dto.Should().NotBeNull();
        _limiter.SecondsLocked("login").Should().Be(0, "the shared limiter never saw these attempts");
    }

    // ---- ManagerAuthorizationService: approvals ----

    [Fact]
    public async Task Manager_FourWrongPins_CorrectPinStillWorks()
    {
        await FailManagerAsync(4);

        var dto = await _auth.AuthorizeWithPinAsync(Request(), ManagerPin);

        dto.ManagerUserId.Should().Be(_manager.Id);
    }

    [Fact]
    public async Task Manager_FiveWrongPins_SixthCallThrowsLocked_AndWritesNoOverride_EvenWithCorrectPin()
    {
        await FailManagerAsync(5);

        var act = () => _auth.AuthorizeWithPinAsync(Request(), ManagerPin);

        var ex = await act.Should().ThrowAsync<PinLockedException>();
        ex.Which.SecondsLeft.Should().Be(30);
        (await _db.ManagerOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Manager_WhileLocked_ThrowsPinLocked_NotUnauthorized()
    {
        await FailManagerAsync(5);

        var ex = await Record.ExceptionAsync(() => _auth.AuthorizeWithPinAsync(Request(), WrongPin));

        ex.Should().BeOfType<PinLockedException>("the UI shows a countdown, not 'PIN incorrecto'");
        ex.Should().NotBeOfType<UnauthorizedAccessException>();
        ex!.Message.Should().Contain("intentos");
    }

    [Fact]
    public async Task Manager_ValidCashierPin_CountsAsFailure()
    {
        await FailManagerAsync(5, pin: CashierPin);

        _limiter.SecondsLocked("manager").Should().Be(30);
        var act = () => _auth.AuthorizeWithPinAsync(Request(), ManagerPin);
        await act.Should().ThrowAsync<PinLockedException>();
    }

    [Fact]
    public async Task Manager_Success_ResetsFailureCounter()
    {
        await FailManagerAsync(4);
        await _auth.AuthorizeWithPinAsync(Request(), ManagerPin);

        await FailManagerAsync(4);

        _limiter.SecondsLocked("manager").Should().Be(0);
        var dto = await _auth.AuthorizeWithPinAsync(Request(), ManagerPin);
        dto.ManagerName.Should().Be("Gerente");
    }

    [Fact]
    public async Task Manager_After30Seconds_CorrectPinWorksAgain()
    {
        await FailManagerAsync(5);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var dto = await _auth.AuthorizeWithPinAsync(Request(), ManagerPin);

        dto.ManagerName.Should().Be("Gerente");
        _limiter.SecondsLocked("manager").Should().Be(0);
    }

    [Fact]
    public async Task Manager_AuthorizeAsUser_WorksWhileLocked()
    {
        await FailManagerAsync(5);
        _limiter.SecondsLocked("manager").Should().Be(30);

        var dto = await _auth.AuthorizeAsUserAsync(Request(), _manager.Id);

        dto.ManagerUserId.Should().Be(_manager.Id);
        (await _db.ManagerOverrides.CountAsync()).Should().Be(1);
        _limiter.SecondsLocked("manager").Should().Be(30, "the signed-in flow neither counts nor resets PIN attempts");
    }

    [Fact]
    public async Task Manager_ServiceWithoutLimiter_NeverLocks()
    {
        var unprotected = new ManagerAuthorizationService(_db, new AuthorizationSettings(), _clock);
        for (var i = 0; i < 10; i++)
        {
            var act = () => unprotected.AuthorizeWithPinAsync(Request(), WrongPin);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        var dto = await unprotected.AuthorizeWithPinAsync(Request(), ManagerPin);

        dto.ManagerName.Should().Be("Gerente");
        _limiter.SecondsLocked("manager").Should().Be(0);
    }
}
