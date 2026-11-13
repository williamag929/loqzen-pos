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

/// <summary>Manager approval of sensitive actions: PIN check, role check, audit record (ManagerOverride).</summary>
public class ManagerAuthorizationServiceTests : IDisposable
{
    private const string ManagerPin = "5555";
    private const string CashierPin = "1111";
    private const string InactiveManagerPin = "9999";

    private readonly PosDbContext _db = TestDbContextFactory.Create();
    private readonly TestClock _clock = new();
    private readonly AuthorizationSettings _settings = new();
    private readonly ManagerAuthorizationService _svc;

    private readonly User _manager;
    private readonly User _cashier;
    private readonly User _inactiveManager;

    public ManagerAuthorizationServiceTests()
    {
        _manager = new User { Name = "Gerente", PinHash = PinHasher.Hash(ManagerPin), Role = UserRole.Manager };
        _cashier = new User { Name = "Cajero", PinHash = PinHasher.Hash(CashierPin), Role = UserRole.Cashier };
        _inactiveManager = new User { Name = "ExGerente", PinHash = PinHasher.Hash(InactiveManagerPin), Role = UserRole.Manager, IsActive = false };
        _db.Users.AddRange(_manager, _cashier, _inactiveManager);
        _db.SaveChanges();

        _svc = new ManagerAuthorizationService(_db, _settings, _clock);
    }

    public void Dispose() => _db.Dispose();

    private static AuthorizationRequest ReturnRequest() => new(
        AuthorizedAction.ReturnItem,
        "Devolución Hamburguesa",
        Amount: 23800m,
        Reference: "CMD-7",
        RequestedByUserId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        RequestedByName: "Cajero Ana");

    // ---- settings ----

    [Fact]
    public void RequiresAuthorization_FollowsSettings()
    {
        _svc.RequiresAuthorization(AuthorizedAction.ReturnItem).Should().BeTrue("default");
        _svc.RequiresAuthorization(AuthorizedAction.CloseShift).Should().BeFalse("not in defaults");

        _settings.ManagerRequiredFor = new List<string> { "CloseShift" };
        _svc.RequiresAuthorization(AuthorizedAction.CloseShift).Should().BeTrue();
        _svc.RequiresAuthorization(AuthorizedAction.ReturnItem).Should().BeFalse();

        _settings.ManagerRequiredFor = new List<string>();
        _svc.RequiresAuthorization(AuthorizedAction.ReturnItem).Should().BeFalse("empty list = nothing requires approval");
    }

    // ---- authorize with PIN ----

    [Fact]
    public async Task AuthorizeWithPin_ManagerPin_RecordsOverride_AndReturnsDto()
    {
        _clock.Advance(TimeSpan.FromMinutes(7));
        var request = ReturnRequest();

        var dto = await _svc.AuthorizeWithPinAsync(request, ManagerPin);

        var now = _clock.GetUtcNow().UtcDateTime;
        dto.Action.Should().Be(AuthorizedAction.ReturnItem);
        dto.ActionLabel.Should().Be("Devolución en venta");
        dto.Description.Should().Be("Devolución Hamburguesa");
        dto.ManagerUserId.Should().Be(_manager.Id);
        dto.ManagerName.Should().Be("Gerente");
        dto.RequestedByName.Should().Be("Cajero Ana");
        dto.Amount.Should().Be(23800m);
        dto.Reference.Should().Be("CMD-7");
        dto.AuthorizedAt.Should().Be(now);

        var stored = await _db.ManagerOverrides.AsNoTracking().SingleAsync();
        stored.Id.Should().Be(dto.Id);
        stored.Action.Should().Be(AuthorizedAction.ReturnItem);
        stored.Description.Should().Be("Devolución Hamburguesa");
        stored.ManagerUserId.Should().Be(_manager.Id);
        stored.ManagerName.Should().Be("Gerente");
        stored.RequestedByUserId.Should().Be(request.RequestedByUserId);
        stored.RequestedByName.Should().Be("Cajero Ana");
        stored.Amount.Should().Be(23800m);
        stored.Reference.Should().Be("CMD-7");
        stored.AuthorizedAt.Should().Be(now);
        stored.CreatedAt.Should().Be(now);
    }

    [Fact]
    public async Task AuthorizeWithPin_AdminPin_AlsoWorks()
    {
        _db.Users.Add(new User { Name = "Admin", PinHash = PinHasher.Hash("1234"), Role = UserRole.Admin });
        await _db.SaveChangesAsync();

        var dto = await _svc.AuthorizeWithPinAsync(ReturnRequest(), "1234");
        dto.ManagerName.Should().Be("Admin");
    }

    [Fact]
    public async Task AuthorizeWithPin_CashierPin_Throws_AndRecordsNothing()
    {
        var act = () => _svc.AuthorizeWithPinAsync(ReturnRequest(), CashierPin);
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*PIN*");
        (await _db.ManagerOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuthorizeWithPin_UnknownPin_ThrowsSameMessageAsWrongRole()
    {
        var wrongRole = await Record.ExceptionAsync(() => _svc.AuthorizeWithPinAsync(ReturnRequest(), CashierPin));
        var unknown = await Record.ExceptionAsync(() => _svc.AuthorizeWithPinAsync(ReturnRequest(), "8080"));

        wrongRole.Should().BeOfType<UnauthorizedAccessException>();
        unknown.Should().BeOfType<UnauthorizedAccessException>();
        unknown!.Message.Should().Be(wrongRole!.Message, "the UI must not leak which PINs exist");
    }

    [Theory]
    [InlineData("12")]
    [InlineData("abcd")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AuthorizeWithPin_MalformedPin_ThrowsUnauthorized(string? pin)
    {
        var act = () => _svc.AuthorizeWithPinAsync(ReturnRequest(), pin!);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task AuthorizeWithPin_InactiveManager_Throws()
    {
        var act = () => _svc.AuthorizeWithPinAsync(ReturnRequest(), InactiveManagerPin);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _db.ManagerOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuthorizeWithPin_NullRequest_Throws()
    {
        var act = () => _svc.AuthorizeWithPinAsync(null!, ManagerPin);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AuthorizeWithPin_KeepsRequestedByFromRequest_DoesNotDefaultToManager()
    {
        var request = new AuthorizationRequest(AuthorizedAction.ApplyDiscount, "10%", Amount: 2000m);
        await _svc.AuthorizeWithPinAsync(request, ManagerPin);

        var stored = await _db.ManagerOverrides.AsNoTracking().SingleAsync();
        stored.RequestedByUserId.Should().BeNull("PIN flow: requester is whoever is signed in, decided by the caller");
        stored.RequestedByName.Should().BeNull();
    }

    // ---- authorize as signed-in user ----

    [Fact]
    public async Task AuthorizeAsUser_ManagerId_RecordsOverride()
    {
        var dto = await _svc.AuthorizeAsUserAsync(ReturnRequest(), _manager.Id);

        dto.ManagerUserId.Should().Be(_manager.Id);
        dto.ManagerName.Should().Be("Gerente");
        dto.RequestedByName.Should().Be("Cajero Ana", "explicit requester is kept");
        (await _db.ManagerOverrides.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AuthorizeAsUser_RequestedByDefaultsToManager_WhenRequestHasNone()
    {
        var request = new AuthorizationRequest(AuthorizedAction.CancelKitchenItem, "Hamburguesa", Reference: "CMD-3");

        var dto = await _svc.AuthorizeAsUserAsync(request, _manager.Id);

        dto.RequestedByName.Should().Be("Gerente");
        var stored = await _db.ManagerOverrides.AsNoTracking().SingleAsync();
        stored.RequestedByUserId.Should().Be(_manager.Id);
        stored.RequestedByName.Should().Be("Gerente");
    }

    [Fact]
    public async Task AuthorizeAsUser_CashierId_Throws()
    {
        var act = () => _svc.AuthorizeAsUserAsync(ReturnRequest(), _cashier.Id);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _db.ManagerOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AuthorizeAsUser_InactiveManagerId_Throws()
    {
        var act = () => _svc.AuthorizeAsUserAsync(ReturnRequest(), _inactiveManager.Id);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task AuthorizeAsUser_UnknownId_Throws()
    {
        var act = () => _svc.AuthorizeAsUserAsync(ReturnRequest(), Guid.NewGuid());
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ---- description fallback ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task BlankDescription_FallsBackToActionLabel(string? description)
    {
        var request = new AuthorizationRequest(AuthorizedAction.CancelKitchenItem, description!);
        var dto = await _svc.AuthorizeWithPinAsync(request, ManagerPin);
        dto.Description.Should().Be("Anular producto en cocina");
    }

    [Fact]
    public async Task Description_IsTrimmed()
    {
        var dto = await _svc.AuthorizeWithPinAsync(new AuthorizationRequest(AuthorizedAction.ApplyDiscount, "  10%  "), ManagerPin);
        dto.Description.Should().Be("10%");
    }

    // ---- recent ----

    [Fact]
    public async Task GetRecent_NewestFirst_AndRespectsTake()
    {
        await _svc.AuthorizeWithPinAsync(new AuthorizationRequest(AuthorizedAction.ReturnItem, "primera"), ManagerPin);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _svc.AuthorizeWithPinAsync(new AuthorizationRequest(AuthorizedAction.ApplyDiscount, "segunda"), ManagerPin);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _svc.AuthorizeAsUserAsync(new AuthorizationRequest(AuthorizedAction.CancelKitchenItem, "tercera"), _manager.Id);

        var all = await _svc.GetRecentAsync();
        all.Select(o => o.Description).Should().Equal("tercera", "segunda", "primera");

        var two = await _svc.GetRecentAsync(take: 2);
        two.Select(o => o.Description).Should().Equal("tercera", "segunda");

        (await _svc.GetRecentAsync(take: 0)).Should().BeEmpty();
        (await _svc.GetRecentAsync(take: -1)).Should().BeEmpty();
    }
}
