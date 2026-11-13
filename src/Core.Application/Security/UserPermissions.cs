using Core.Domain.Enums;

namespace Core.Application.Security;

/// <summary>Screens of the desktop app, for role-based visibility.</summary>
public enum PosView { Checkout, Comandas, Kds, CashShift, Masters }

/// <summary>
/// Single place for "who can do what". Roles are ordered by privilege (see <see cref="UserRole"/>).
/// </summary>
public static class UserPermissions
{
    public static bool CanAccess(UserRole role, PosView view) => view switch
    {
        PosView.Kds => true,
        PosView.Comandas => role >= UserRole.Waiter,
        PosView.Checkout => role >= UserRole.Cashier,
        PosView.CashShift => role >= UserRole.Cashier,
        PosView.Masters => role >= UserRole.Manager,
        _ => false
    };

    /// <summary>Can approve returns, discounts, cancelling dishes already in the kitchen...</summary>
    public static bool CanAuthorize(UserRole role) => role >= UserRole.Manager;

    public static bool CanManageUsers(UserRole role) => role >= UserRole.Manager;

    /// <summary>Only an Admin can create or promote another Admin.</summary>
    public static bool CanAssignRole(UserRole actor, UserRole target) =>
        CanManageUsers(actor) && (target < UserRole.Admin || actor == UserRole.Admin);

    public static string Label(UserRole role) => role switch
    {
        UserRole.Kitchen => "Cocina",
        UserRole.Waiter => "Mesero",
        UserRole.Cashier => "Cajero",
        UserRole.Manager => "Gerente",
        UserRole.Admin => "Administrador",
        _ => role.ToString()
    };

    public static string Label(AuthorizedAction action) => action switch
    {
        AuthorizedAction.ReturnItem => "Devolución en venta",
        AuthorizedAction.ApplyDiscount => "Descuento",
        AuthorizedAction.CancelKitchenItem => "Anular producto en cocina",
        AuthorizedAction.OpenCashDrawer => "Abrir cajón sin venta",
        AuthorizedAction.CloseShift => "Cerrar turno",
        _ => action.ToString()
    };
}
