using Core.Application.Security;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>Staff users: creation rules, PIN uniqueness among active users, PIN login, lock-out protection.</summary>
public class UserServiceTests : IDisposable
{
    private readonly PosDbContext _db = TestDbContextFactory.Create();
    private readonly TestClock _clock = new();
    private readonly UserService _svc;

    public UserServiceTests() => _svc = new UserService(_db, _clock);
    public void Dispose() => _db.Dispose();

    // ---- create ----

    [Fact]
    public async Task Create_ReturnsDtoWithoutHash_AndStoresHashedPin()
    {
        var dto = await _svc.CreateAsync("  Gerente  ", "5555", UserRole.Manager);

        dto.Name.Should().Be("Gerente", "name is trimmed");
        dto.Role.Should().Be(UserRole.Manager);
        dto.IsActive.Should().BeTrue();
        dto.LastLoginAt.Should().BeNull();
        dto.CreatedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        dto.CanAuthorize.Should().BeTrue();
        dto.RoleLabel.Should().Be("Gerente");

        var entity = await _db.Users.AsNoTracking().SingleAsync(u => u.Id == dto.Id);
        entity.PinHash.Should().StartWith("pbkdf2$").And.NotContain("5555");
        PinHasher.Verify("5555", entity.PinHash).Should().BeTrue();
    }

    [Theory]
    [InlineData("", "1234")]
    [InlineData("   ", "1234")]
    [InlineData(null, "1234")]
    [InlineData("Ana", "123")]
    [InlineData("Ana", "1234567")]
    [InlineData("Ana", "abcd")]
    [InlineData("Ana", null)]
    public async Task Create_InvalidNameOrPin_ThrowsArgumentException(string? name, string? pin)
    {
        var act = () => _svc.CreateAsync(name!, pin!, UserRole.Cashier);
        await act.Should().ThrowAsync<ArgumentException>();
        (await _db.Users.CountAsync()).Should().Be(0, "nothing is persisted");
    }

    [Fact]
    public async Task Create_NameTooLong_Throws()
    {
        var act = () => _svc.CreateAsync(new string('a', 101), "1234", UserRole.Cashier);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*nombre*");
    }

    [Fact]
    public async Task Create_DuplicatePinAmongActiveUsers_Throws()
    {
        await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        var act = () => _svc.CreateAsync("Beto", "1234", UserRole.Waiter);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PIN*");
        (await _db.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_SamePinAsInactiveUser_IsAllowed()
    {
        var ana = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        await _svc.UpdateAsync(ana.Id, ana.Name, ana.Role, isActive: false);

        var beto = await _svc.CreateAsync("Beto", "1234", UserRole.Waiter);
        beto.IsActive.Should().BeTrue();
        (await _svc.AuthenticateByPinAsync("1234"))!.Id.Should().Be(beto.Id, "login is unambiguous: only Beto is active");
    }

    // ---- authenticate ----

    [Fact]
    public async Task Authenticate_CorrectPin_ReturnsUser_AndSetsLastLoginAtToClock()
    {
        var created = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        _clock.Advance(TimeSpan.FromHours(2));

        var dto = await _svc.AuthenticateByPinAsync("1234");

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(created.Id);
        dto.LastLoginAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        (await _db.Users.AsNoTracking().SingleAsync()).LastLoginAt.Should().Be(_clock.GetUtcNow().UtcDateTime, "persisted");
    }

    [Fact]
    public async Task Authenticate_WrongPin_ReturnsNull()
    {
        await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        (await _svc.AuthenticateByPinAsync("4321")).Should().BeNull();
        (await _db.Users.SingleAsync()).LastLoginAt.Should().BeNull();
    }

    [Fact]
    public async Task Authenticate_InactiveUser_ReturnsNull()
    {
        var ana = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        await _svc.UpdateAsync(ana.Id, ana.Name, ana.Role, isActive: false);
        (await _svc.AuthenticateByPinAsync("1234")).Should().BeNull();
    }

    [Theory]
    [InlineData("12")]
    [InlineData("abcd")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Authenticate_MalformedPin_ReturnsNull_NoThrow(string? pin)
    {
        await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        var act = () => _svc.AuthenticateByPinAsync(pin!);
        (await act.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    // ---- change pin ----

    [Fact]
    public async Task ChangePin_OldPinFails_NewPinWorks()
    {
        var ana = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        await _svc.ChangePinAsync(ana.Id, "9876");

        (await _svc.AuthenticateByPinAsync("1234")).Should().BeNull();
        (await _svc.AuthenticateByPinAsync("9876"))!.Id.Should().Be(ana.Id);
    }

    [Fact]
    public async Task ChangePin_ToPinOfAnotherActiveUser_Throws()
    {
        var ana = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        await _svc.CreateAsync("Beto", "5678", UserRole.Waiter);

        var act = () => _svc.ChangePinAsync(ana.Id, "5678");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PIN*");
        (await _svc.AuthenticateByPinAsync("1234"))!.Id.Should().Be(ana.Id, "old PIN still valid after the failed change");
    }

    [Fact]
    public async Task ChangePin_ToOwnCurrentPin_IsAllowed()
    {
        var ana = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        await _svc.ChangePinAsync(ana.Id, "1234");
        (await _svc.AuthenticateByPinAsync("1234"))!.Id.Should().Be(ana.Id);
    }

    [Fact]
    public async Task ChangePin_InvalidPin_Throws()
    {
        var ana = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        var act = () => _svc.ChangePinAsync(ana.Id, "12");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ChangePin_UnknownUser_Throws()
    {
        var act = () => _svc.ChangePinAsync(Guid.NewGuid(), "1234");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no encontrado*");
    }

    // ---- update / lock-out protection ----

    [Fact]
    public async Task Update_CannotDeactivateLastAuthorizer()
    {
        var admin = await _svc.CreateAsync("Admin", "1234", UserRole.Admin);
        await _svc.CreateAsync("Cajero", "1111", UserRole.Cashier);

        var act = () => _svc.UpdateAsync(admin.Id, admin.Name, admin.Role, isActive: false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorizar*");
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == admin.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Update_CannotDemoteLastAuthorizerBelowManager()
    {
        var admin = await _svc.CreateAsync("Admin", "1234", UserRole.Admin);

        var act = () => _svc.UpdateAsync(admin.Id, admin.Name, UserRole.Cashier, isActive: true);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorizar*");
        (await _db.Users.AsNoTracking().SingleAsync()).Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task Update_LastAuthorizer_CanMoveBetweenManagerAndAdmin()
    {
        var admin = await _svc.CreateAsync("Admin", "1234", UserRole.Admin);
        var dto = await _svc.UpdateAsync(admin.Id, "Jefe", UserRole.Manager, isActive: true);
        dto.Role.Should().Be(UserRole.Manager, "still an authorizer, so the change is allowed");
        dto.Name.Should().Be("Jefe");
    }

    [Fact]
    public async Task Update_CanDeactivateAuthorizer_WhenAnotherActiveOneRemains()
    {
        var admin = await _svc.CreateAsync("Admin", "1234", UserRole.Admin);
        await _svc.CreateAsync("Gerente", "5555", UserRole.Manager);

        var dto = await _svc.UpdateAsync(admin.Id, admin.Name, admin.Role, isActive: false);
        dto.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Update_CanDeactivateCashier()
    {
        await _svc.CreateAsync("Admin", "1234", UserRole.Admin);
        var cashier = await _svc.CreateAsync("Cajero", "1111", UserRole.Cashier);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var dto = await _svc.UpdateAsync(cashier.Id, "Cajero 2", UserRole.Waiter, isActive: false);

        dto.IsActive.Should().BeFalse();
        dto.Role.Should().Be(UserRole.Waiter);
        dto.Name.Should().Be("Cajero 2");
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == cashier.Id)).UpdatedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Update_UnknownUser_Throws()
    {
        var act = () => _svc.UpdateAsync(Guid.NewGuid(), "X", UserRole.Cashier, true);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no encontrado*");
    }

    // ---- queries ----

    [Fact]
    public async Task GetAll_ExcludesInactiveByDefault_IncludesWithFlag()
    {
        await _svc.CreateAsync("Admin", "1234", UserRole.Admin);
        var cashier = await _svc.CreateAsync("Cajero", "1111", UserRole.Cashier);
        await _svc.CreateAsync("Mesero", "2222", UserRole.Waiter);
        await _svc.UpdateAsync(cashier.Id, cashier.Name, cashier.Role, isActive: false);

        var active = await _svc.GetAllAsync();
        active.Select(u => u.Name).Should().BeEquivalentTo("Admin", "Mesero");
        active.Select(u => u.Role).Should().BeInDescendingOrder("highest privilege first");

        var all = await _svc.GetAllAsync(includeInactive: true);
        all.Should().HaveCount(3);
        all.Single(u => u.Name == "Cajero").IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task GetById_And_AnyActive()
    {
        (await _svc.AnyActiveAsync()).Should().BeFalse();
        (await _svc.GetByIdAsync(Guid.NewGuid())).Should().BeNull();

        var ana = await _svc.CreateAsync("Ana", "1234", UserRole.Cashier);
        (await _svc.AnyActiveAsync()).Should().BeTrue();
        (await _svc.GetByIdAsync(ana.Id))!.Name.Should().Be("Ana");
    }
}
