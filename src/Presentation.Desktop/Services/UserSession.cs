using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Application.DTOs;
using Core.Application.Security;
using Core.Application.Services;
using Core.Domain.Enums;

namespace Presentation.Desktop.Services;

/// <summary>
/// Who is signed in on this terminal. Bindable (nav tabs hide per role) and shared app-wide.
/// When nobody is signed in (RequireLogin = false → open terminal) every screen is reachable.
/// </summary>
public partial class UserSession : ObservableObject, IUserSession
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn), nameof(DisplayName), nameof(RoleLabel), nameof(ChipText), nameof(IsManager),
        nameof(CanAccessCheckout), nameof(CanAccessComandas), nameof(CanAccessKds), nameof(CanAccessCashShift),
        nameof(CanAccessMasters), nameof(CanAccessBillPayments), nameof(CanManageUsers), nameof(EffectiveRole))]
    private UserDto? _currentUser;

    public event Action? Changed;

    public bool IsLoggedIn => CurrentUser != null;
    public string DisplayName => CurrentUser?.Name ?? string.Empty;
    public string RoleLabel => CurrentUser?.RoleLabel ?? string.Empty;
    public string ChipText => $"👤 {DisplayName} · {RoleLabel}";
    public bool IsManager => CurrentUser?.CanAuthorize ?? false;

    /// <summary>Role used for permission checks; an open terminal (nobody signed in) behaves as Admin.</summary>
    public UserRole EffectiveRole => CurrentUser?.Role ?? UserRole.Admin;

    public bool CanAccessCheckout  => CanAccess(PosView.Checkout);
    public bool CanAccessComandas  => CanAccess(PosView.Comandas);
    public bool CanAccessKds       => CanAccess(PosView.Kds);
    public bool CanAccessCashShift => CanAccess(PosView.CashShift);
    public bool CanAccessMasters   => CanAccess(PosView.Masters);
    public bool CanAccessBillPayments => CanAccess(PosView.BillPayments);
    public bool CanManageUsers     => CurrentUser == null || UserPermissions.CanManageUsers(CurrentUser.Role);

    public bool CanAccess(PosView view) => CurrentUser == null || UserPermissions.CanAccess(CurrentUser.Role, view);

    public void SignIn(UserDto user)
    {
        CurrentUser = user ?? throw new ArgumentNullException(nameof(user));
        Changed?.Invoke();
    }

    public void SignOut()
    {
        if (CurrentUser == null) return;
        CurrentUser = null;
        Changed?.Invoke();
    }
}
