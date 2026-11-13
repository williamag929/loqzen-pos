using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Security;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

/// <summary>
/// Manager approval for sensitive actions. Every approval is persisted as a <see cref="ManagerOverride"/>.
/// Wrong PIN and "user cannot authorize" share one message so the UI does not leak which PINs exist.
/// PIN approvals go through an optional <see cref="IPinAttemptLimiter"/> (scope "manager", independent from
/// login): per terminal and in-memory, so restarting the app clears it. Good enough for a touch POS; the goal
/// is to stop casual PIN guessing, not to be a hardened auth server.
/// </summary>
public class ManagerAuthorizationService : IManagerAuthorizationService
{
    private const string UnauthorizedMessage = "PIN de gerente incorrecto o sin permiso para autorizar.";
    private const string ManagerScope = "manager";

    private readonly PosDbContext _db;
    private readonly AuthorizationSettings _settings;
    private readonly TimeProvider _clock;
    private readonly IPinAttemptLimiter? _limiter;

    /// <param name="limiter">Optional: locks PIN approval after repeated failures (see <see cref="PinAttemptLimiter"/>).</param>
    public ManagerAuthorizationService(PosDbContext db, AuthorizationSettings settings, TimeProvider clock, IPinAttemptLimiter? limiter = null)
    {
        _db = db;
        _settings = settings;
        _clock = clock;
        _limiter = limiter;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public bool RequiresAuthorization(AuthorizedAction action) => _settings.Requires(action);

    /// <summary>
    /// Throws <see cref="PinLockedException"/> while approvals are locked after too many failures (even for a
    /// valid manager PIN). A wrong PIN and a valid PIN of a user who cannot authorize both count as failures.
    /// </summary>
    public async Task<ManagerOverrideDto> AuthorizeWithPinAsync(AuthorizationRequest request, string pin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _limiter?.EnsureNotLocked(ManagerScope);

        User? manager = null;
        if (PinHasher.IsValidPin(pin))
        {
            var active = await _db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(ct);
            manager = active.FirstOrDefault(u => PinHasher.Verify(pin, u.PinHash));
        }

        if (manager == null || !UserPermissions.CanAuthorize(manager.Role))
        {
            _limiter?.RegisterFailure(ManagerScope);
            throw new UnauthorizedAccessException(UnauthorizedMessage);
        }

        var dto = await RecordAsync(request, manager, ct);
        _limiter?.RegisterSuccess(ManagerScope);
        return dto;
    }

    public async Task<ManagerOverrideDto> AuthorizeAsUserAsync(AuthorizationRequest request, Guid managerUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var manager = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == managerUserId, ct);
        if (manager == null || !manager.IsActive || !UserPermissions.CanAuthorize(manager.Role))
            throw new UnauthorizedAccessException(UnauthorizedMessage);

        // The manager approved their own action: record them as the requester unless told otherwise.
        if (request.RequestedByUserId == null && request.RequestedByName == null)
            request = request with { RequestedByUserId = manager.Id, RequestedByName = manager.Name };

        return await RecordAsync(request, manager, ct);
    }

    public async Task<IReadOnlyList<ManagerOverrideDto>> GetRecentAsync(int take = 50, CancellationToken ct = default)
    {
        if (take <= 0) return Array.Empty<ManagerOverrideDto>();
        var list = await _db.ManagerOverrides.AsNoTracking()
            .OrderByDescending(o => o.AuthorizedAt)
            .ThenByDescending(o => o.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        return list.Select(MapToDto).ToList();
    }

    private async Task<ManagerOverrideDto> RecordAsync(AuthorizationRequest request, User manager, CancellationToken ct)
    {
        var now = UtcNow;
        var entry = new ManagerOverride
        {
            Action = request.Action,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? UserPermissions.Label(request.Action)
                : request.Description.Trim(),
            ManagerUserId = manager.Id,
            ManagerName = manager.Name,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByName = request.RequestedByName,
            Amount = request.Amount,
            Reference = request.Reference,
            AuthorizedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.ManagerOverrides.Add(entry);
        await _db.SaveChangesAsync(ct);
        return MapToDto(entry);
    }

    private static ManagerOverrideDto MapToDto(ManagerOverride o) => new()
    {
        Id = o.Id,
        Action = o.Action,
        Description = o.Description,
        ManagerUserId = o.ManagerUserId,
        ManagerName = o.ManagerName,
        RequestedByName = o.RequestedByName,
        Amount = o.Amount,
        Reference = o.Reference,
        AuthorizedAt = o.AuthorizedAt
    };
}
