using Core.Application.DTOs;
using Core.Application.Security;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Security;

/// <summary>Role → screen visibility, who can authorize/manage users, and which actions need a manager.</summary>
public class UserPermissionsTests
{
    private static readonly PosView[] AllViews = Enum.GetValues<PosView>();

    [Fact]
    public void Kitchen_SeesOnlyKds()
    {
        AllViews.Where(v => UserPermissions.CanAccess(UserRole.Kitchen, v))
            .Should().Equal(PosView.Kds);
    }

    [Fact]
    public void Waiter_SeesComandasAndKds_NotCheckout()
    {
        UserPermissions.CanAccess(UserRole.Waiter, PosView.Comandas).Should().BeTrue();
        UserPermissions.CanAccess(UserRole.Waiter, PosView.Kds).Should().BeTrue();
        UserPermissions.CanAccess(UserRole.Waiter, PosView.Checkout).Should().BeFalse();
        UserPermissions.CanAccess(UserRole.Waiter, PosView.CashShift).Should().BeFalse();
        UserPermissions.CanAccess(UserRole.Waiter, PosView.Masters).Should().BeFalse();
    }

    [Fact]
    public void Cashier_GetsCheckoutAndCashShift_NotMasters()
    {
        UserPermissions.CanAccess(UserRole.Cashier, PosView.Checkout).Should().BeTrue();
        UserPermissions.CanAccess(UserRole.Cashier, PosView.CashShift).Should().BeTrue();
        UserPermissions.CanAccess(UserRole.Cashier, PosView.Comandas).Should().BeTrue();
        UserPermissions.CanAccess(UserRole.Cashier, PosView.Kds).Should().BeTrue();
        UserPermissions.CanAccess(UserRole.Cashier, PosView.Masters).Should().BeFalse();
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Admin)]
    public void ManagerAndAdmin_SeeEverything(UserRole role) =>
        AllViews.Should().OnlyContain(v => UserPermissions.CanAccess(role, v));

    [Theory]
    [InlineData(UserRole.Kitchen, false)]
    [InlineData(UserRole.Waiter, false)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Admin, true)]
    public void CanAuthorize_OnlyManagerAndAbove(UserRole role, bool expected)
    {
        UserPermissions.CanAuthorize(role).Should().Be(expected);
        UserPermissions.CanManageUsers(role).Should().Be(expected);
    }

    [Fact]
    public void CanAssignRole_ManagerCannotCreateAdmin_AdminCan()
    {
        UserPermissions.CanAssignRole(UserRole.Manager, UserRole.Admin).Should().BeFalse();
        UserPermissions.CanAssignRole(UserRole.Admin, UserRole.Admin).Should().BeTrue();

        UserPermissions.CanAssignRole(UserRole.Manager, UserRole.Manager).Should().BeTrue();
        UserPermissions.CanAssignRole(UserRole.Manager, UserRole.Cashier).Should().BeTrue();
        UserPermissions.CanAssignRole(UserRole.Cashier, UserRole.Waiter).Should().BeFalse("cashiers do not manage users");
    }

    [Fact]
    public void Labels_AreSpanish()
    {
        UserPermissions.Label(UserRole.Manager).Should().Be("Gerente");
        UserPermissions.Label(UserRole.Admin).Should().Be("Administrador");
        UserPermissions.Label(AuthorizedAction.CancelKitchenItem).Should().Be("Anular producto en cocina");
    }

    [Fact]
    public void AuthorizationSettings_NullList_UsesDefaults()
    {
        var s = new AuthorizationSettings { ManagerRequiredFor = null };
        s.Requires(AuthorizedAction.ReturnItem).Should().BeTrue();
        s.Requires(AuthorizedAction.ApplyDiscount).Should().BeTrue();
        s.Requires(AuthorizedAction.CancelKitchenItem).Should().BeTrue();
        s.Requires(AuthorizedAction.OpenCashDrawer).Should().BeFalse();
        s.Requires(AuthorizedAction.CloseShift).Should().BeFalse();
    }

    [Fact]
    public void AuthorizationSettings_ExplicitList_IsCaseInsensitive_AndReplacesDefaults()
    {
        var s = new AuthorizationSettings { ManagerRequiredFor = new List<string> { "closeshift", "OPENCASHDRAWER" } };
        s.Requires(AuthorizedAction.CloseShift).Should().BeTrue();
        s.Requires(AuthorizedAction.OpenCashDrawer).Should().BeTrue();
        s.Requires(AuthorizedAction.ReturnItem).Should().BeFalse("defaults do not apply once a list is given");
        s.Requires(AuthorizedAction.CancelKitchenItem).Should().BeFalse();
    }

    [Fact]
    public void AuthorizationSettings_EmptyList_RequiresNothing()
    {
        var s = new AuthorizationSettings { ManagerRequiredFor = new List<string>() };
        Enum.GetValues<AuthorizedAction>().Should().OnlyContain(a => !s.Requires(a));
    }
}
