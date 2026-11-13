using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Security;
using Core.Domain.Enums;

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

    public string UserFormTitle => IsEditingUser ? "👥 Editar Usuario" : "👥 Nuevo Usuario";
    public string UserPinHint => IsEditingUser
        ? $"PIN ({PinHasher.MinLength}-{PinHasher.MaxLength} dígitos) · deje vacío para conservar el actual"
        : $"PIN * ({PinHasher.MinLength}-{PinHasher.MaxLength} dígitos)";

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
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
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
            Notification = $"⚠️ No puede editar a un {user.RoleLabel}.";
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
            Notification = "⚠️ El nombre es obligatorio.";
            return;
        }
        if (UserRoleOption is not { } roleOption)
        {
            Notification = "⚠️ Seleccione un rol.";
            return;
        }
        var pin = UserPin.Trim();
        var hasPin = !string.IsNullOrEmpty(pin);
        if (!IsEditingUser && !hasPin)
        {
            Notification = "⚠️ El PIN es obligatorio para un usuario nuevo.";
            return;
        }
        if (hasPin && !PinHasher.IsValidPin(pin))
        {
            Notification = $"⚠️ El PIN debe tener entre {PinHasher.MinLength} y {PinHasher.MaxLength} dígitos.";
            return;
        }

        try
        {
            if (IsEditingUser)
            {
                var updated = await _users.UpdateAsync(EditUserId, name, roleOption.Role, UserIsActive);
                if (hasPin) await _users.ChangePinAsync(EditUserId, pin);
                Notification = hasPin ? $"✅ Usuario '{updated.Name}' actualizado (PIN cambiado)." : $"✅ Usuario '{updated.Name}' actualizado.";
            }
            else
            {
                var created = await _users.CreateAsync(name, pin, roleOption.Role);
                if (!UserIsActive) await _users.UpdateAsync(created.Id, created.Name, created.Role, false);
                Notification = $"✅ Usuario '{created.Name}' creado.";
            }

            UserPin = string.Empty;
            IsUserFormVisible = false;
            await LoadUsersAsync();
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
    }

    /// <summary>Soft delete: users are never removed (audit trail), only deactivated / reactivated.</summary>
    [RelayCommand]
    public async Task ToggleUserActiveAsync(UserDto user)
    {
        if (user.Id == _session.CurrentUser?.Id && user.IsActive)
        {
            Notification = "⚠️ No puede desactivar su propio usuario.";
            return;
        }
        try
        {
            await _users.UpdateAsync(user.Id, user.Name, user.Role, !user.IsActive);
            Notification = user.IsActive ? $"🚫 Usuario '{user.Name}' desactivado." : $"✅ Usuario '{user.Name}' reactivado.";
            await LoadUsersAsync();
        }
        catch (Exception ex) { Notification = $"❌ {ex.Message}"; }
    }

    [RelayCommand]
    public void CancelUserForm()
    {
        UserPin = string.Empty;
        IsUserFormVisible = false;
    }
}
