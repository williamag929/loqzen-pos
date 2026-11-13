using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Core.Application.Services;

/// <summary>
/// Manager approval for sensitive actions (returns, discounts, cancelling dishes in the kitchen).
/// Every approval is persisted as a <c>ManagerOverride</c> (audit trail).
/// </summary>
public interface IManagerAuthorizationService
{
    /// <summary>From configuration: does this action need a manager?</summary>
    bool RequiresAuthorization(AuthorizedAction action);

    /// <summary>
    /// Validates the PIN against active users with a role that can authorize and records the override.
    /// Throws UnauthorizedAccessException when the PIN is wrong or the user cannot authorize.
    /// </summary>
    Task<ManagerOverrideDto> AuthorizeWithPinAsync(AuthorizationRequest request, string pin, CancellationToken ct = default);

    /// <summary>
    /// The signed-in user is already a manager: record the override without asking for a PIN.
    /// Throws UnauthorizedAccessException if that user cannot authorize.
    /// </summary>
    Task<ManagerOverrideDto> AuthorizeAsUserAsync(AuthorizationRequest request, Guid managerUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ManagerOverrideDto>> GetRecentAsync(int take = 50, CancellationToken ct = default);
}
