using System;
using Core.Domain.Common;
using Core.Domain.Enums;

namespace Core.Domain.Entities;

/// <summary>
/// Staff member who signs in with a numeric PIN. The PIN is never stored: only a salted
/// PBKDF2 hash (see Core.Application.Security.PinHasher). Active users must have distinct PINs
/// so that PIN-only login (touch screen, no username) is unambiguous.
/// </summary>
public class User : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Cashier;
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// Audit record: a manager approved a sensitive action (return, discount, cancel a dish already in
/// the kitchen...). Written in the same SaveChanges as the action whenever possible.
/// </summary>
public class ManagerOverride : BaseEntity
{
    public AuthorizedAction Action { get; set; }
    public string Description { get; set; } = string.Empty;

    public Guid ManagerUserId { get; set; }
    public string ManagerName { get; set; } = string.Empty;

    /// <summary>Who asked for the approval (the logged-in cashier/waiter), if known.</summary>
    public Guid? RequestedByUserId { get; set; }
    public string? RequestedByName { get; set; }

    /// <summary>Money involved (discount amount, returned value), if applicable.</summary>
    public decimal? Amount { get; set; }

    /// <summary>Free reference: order number, comanda number, product name.</summary>
    public string? Reference { get; set; }

    public DateTime AuthorizedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// An override is single-use: it is consumed by the FIRST sensitive operation that verifies it
    /// (set in the same SaveChanges as that operation). Null = still available.
    /// </summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>What consumed it, free text (max 200), e.g. "ComandaItem &lt;id&gt;".</summary>
    public string? ConsumedFor { get; set; }
}
