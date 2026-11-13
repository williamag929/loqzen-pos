using System;
using System.Collections.Generic;
using System.Linq;
using Core.Application.Security;
using Core.Domain.Enums;

namespace Core.Application.DTOs;

public class UserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public string RoleLabel => UserPermissions.Label(Role);
    public bool CanAuthorize => UserPermissions.CanAuthorize(Role);
}

public class ManagerOverrideDto
{
    public Guid Id { get; set; }
    public AuthorizedAction Action { get; set; }
    public string ActionLabel => UserPermissions.Label(Action);
    public string Description { get; set; } = string.Empty;
    public Guid ManagerUserId { get; set; }
    public string ManagerName { get; set; } = string.Empty;
    public string? RequestedByName { get; set; }
    public decimal? Amount { get; set; }
    public string? Reference { get; set; }
    public DateTime AuthorizedAt { get; set; }
}

/// <summary>What is being asked for approval. Who approves is decided by the service.</summary>
public sealed record AuthorizationRequest(
    AuthorizedAction Action,
    string Description,
    decimal? Amount = null,
    string? Reference = null,
    Guid? RequestedByUserId = null,
    string? RequestedByName = null);

/// <summary>Bound from appsettings.json section "Authorization".</summary>
public class AuthorizationSettings
{
    /// <summary>Show the PIN login screen at startup and when the user signs out.</summary>
    public bool RequireLogin { get; set; } = true;

    /// <summary>
    /// Actions that need a manager PIN (names of <see cref="AuthorizedAction"/>).
    /// Null = defaults (ReturnItem, ApplyDiscount, CancelKitchenItem). Empty list = nothing requires approval.
    /// </summary>
    public List<string>? ManagerRequiredFor { get; set; }

    public static readonly IReadOnlyList<AuthorizedAction> Defaults = new[]
    {
        AuthorizedAction.ReturnItem, AuthorizedAction.ApplyDiscount, AuthorizedAction.CancelKitchenItem
    };

    public bool Requires(AuthorizedAction action) =>
        ManagerRequiredFor == null
            ? Defaults.Contains(action)
            : ManagerRequiredFor.Any(n => string.Equals(n, action.ToString(), StringComparison.OrdinalIgnoreCase));
}
