using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Security;
using Core.Domain.Enums;
using Presentation.Desktop.Localization;

namespace Presentation.Desktop.ViewModels;

/// <summary>Role choice for the user form (Spanish label, enum behind it).</summary>
public sealed record RoleOptionViewModel(UserRole Role, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Users tab: staff with PIN login. Visible to managers; only an Admin may create another Admin.</summary>
public partial class MastersViewModel
{
    public bool IsUsersTab => ActiveTab == MastersTab.Users;

    /// <summary>Tab button visibility (follows the signed-in user).</summary>
    public bool CanManageUsers => _session.CurrentUser == null || UserPermissions.CanManageUsers(_session.CurrentUser.Role);

    public ObservableCollection<UserDto> Users { get; } = new();
    public ObservableCollection<RoleOptionViewModel> RoleOptions { get; } = new();

    // ── User Edit Form ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserFormTitle), nameof(UserPinHint))]
    private bool _isUserFormVisible = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserFormTitle), nameof(UserPinHint))]
    private bool _isEditingUser = false;

    public string UserFormTitle => IsEditingUser ? T("Masters.Users.FormTitle.Edit") : T("Masters.Users.FormTitle.New");
    public string UserPinHint => IsEditingUser
        ? T("Masters.Users.PinHint.Edit", PinHasher.MinLength, PinHasher.MaxLength)
        : T("Masters.Users.PinHint.New", PinHasher.MinLength, PinHasher.MaxLength);

    [ObservableProperty] private Guid _editUserId;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private RoleOptionViewModel? _userRoleOption;
    [ObservableProperty] private string _userPin = string.Empty;
    [ObservableProperty] private bool _userIsActive = true;

    [RelayCommand]
    public void ShowUsers()
    {
        if (!CanManageUsers) return;
        ActiveTab = MastersTab.Users;
        _ = LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        if (!CanManageUsers) return;
        try
        {
            var items = await _users.GetAllAsync(includeInactive: true);
            Users.Clear();
            foreach (var u in items.OrderBy(u => u.Role).ThenBy(u => u.Name)) Users.Add(u);
        }
        catch (Exception ex) { Notification = T("Masters.Error", ex.Message); }
    }

    private void RebuildRoleOptions()
    {
        var actor = _session.CurrentUser?.Role ?? UserRole.Admin; // open terminal = full rights
        RoleOptions.Clear();
        foreach (var role in Enum.GetValues<UserRole>().Where(r => UserPermissions.CanAssignRole(actor, r)))
            RoleOptions.Add(new RoleOptionViewModel(role, UserPermissions.Label(role)));
    }

    private void OnSessionChangedForUsers()
    {
        OnPropertyChanged(nameof(CanManageUsers));
        if (!CanManageUsers && IsUsersTab) ShowProducts();
    }

    [RelayCommand]
    public void NewUser()
    {
        RebuildRoleOptions();
        IsEditingUser = false;
        EditUserId    = Guid.Empty;
        UserName      = string.Empty;
        UserRoleOption = RoleOptions.FirstOrDefault(r => r.Role == UserRole.Cashier) ?? RoleOptions.FirstOrDefault();
        UserPin       = string.Empty;
        UserIsActive  = true;
        IsUserFormVisible = true;
    }

    [RelayCommand]
    public void EditUser(UserDto user)
    {
        RebuildRoleOptions();
        if (RoleOptions.All(r => r.Role != user.Role))
        {
            Notification = T("Masters.Users.CannotEditRole", user.RoleLabel);
            return;
        }
        IsEditingUser = true;
        EditUserId    = user.Id;
        UserName      = user.Name;
        UserRoleOption = RoleOptions.FirstOrDefault(r => r.Role == user.Role);
        UserPin       = string.Empty;
        UserIsActive  = user.IsActive;
        IsUserFormVisible = true;
    }

    [RelayCommand]
    public async Task SaveUserAsync()
    {
        var name = UserName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Notification = T("Masters.Users.NameRequired");
            return;
        }
        if (UserRoleOption is not { } roleOption)
        {
            Notification = T("Masters.Users.SelectRole");
            return;
        }
        var pin = UserPin.Trim();
        var hasPin = !string.IsNullOrEmpty(pin);
        if (!IsEditingUser && !hasPin)
        {
            Notification = T("Masters.Users.PinRequired");
            return;
        }
        if (hasPin && !PinHasher.IsValidPin(pin))
        {
            Notification = T("Masters.Users.PinLength", PinHasher.MinLength, PinHasher.MaxLength);
            return;
        }

        try
        {
            if (IsEditingUser)
            {
                var updated = await _users.UpdateAsync(EditUserId, name, roleOption.Role, UserIsActive);
                if (hasPin) await _users.ChangePinAsync(EditUserId, pin);
                Notification = hasPin ? T("Masters.Users.UpdatedPin", updated.Name) : T("Masters.Users.Updated", updated.Name);
            }
            else
            {
                var created = await _users.CreateAsync(name, pin, roleOption.Role);
                if (!UserIsActive) await _users.UpdateAsync(created.Id, created.Name, created.Role, false);
                Notification = T("Masters.Users.Created", created.Name);
            }

            UserPin = string.Empty;
            IsUserFormVisible = false;
            await LoadUsersAsync();
        }
        catch (Exception ex) { Notification = T("Masters.Error", ex.Message); }
    }

    /// <summary>Soft delete: users are never removed (audit trail), only deactivated / reactivated.</summary>
    [RelayCommand]
    public async Task ToggleUserActiveAsync(UserDto user)
    {
        if (user.Id == _session.CurrentUser?.Id && user.IsActive)
        {
            Notification = T("Masters.Users.CannotDeactivateSelf");
            return;
        }
        try
        {
            await _users.UpdateAsync(user.Id, user.Name, user.Role, !user.IsActive);
            Notification = user.IsActive ? T("Masters.Users.Deactivated", user.Name) : T("Masters.Users.Reactivated", user.Name);
            await LoadUsersAsync();
        }
        catch (Exception ex) { Notification = T("Masters.Error", ex.Message); }
    }

    [RelayCommand]
    public void CancelUserForm()
    {
        UserPin = string.Empty;
        IsUserFormVisible = false;
    }
}
