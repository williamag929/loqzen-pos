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
/// </summary>
public class ManagerAuthorizationService : IManagerAuthorizationService
{
    private const string UnauthorizedMessage = "PIN de gerente incorrecto o sin permiso para autorizar.";

    private readonly PosDbContext _db;
    private readonly AuthorizationSettings _settings;
    private readonly TimeProvider _clock;

    public ManagerAuthorizationService(PosDbContext db, AuthorizationSettings settings, TimeProvider clock)
    {
        _db = db;
        _settings = settings;
        _clock = clock;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public bool RequiresAuthorization(AuthorizedAction action) => _settings.Requires(action);

    public async Task<ManagerOverrideDto> AuthorizeWithPinAsync(AuthorizationRequest request, string pin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        User? manager = null;
        if (PinHasher.IsValidPin(pin))
        {
            var active = await _db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(ct);
            manager = active.FirstOrDefault(u => PinHasher.Verify(pin, u.PinHash));
        }

        if (manager == null || !UserPermissions.CanAuthorize(manager.Role))
            throw new UnauthorizedAccessException(UnauthorizedMessage);

        return await RecordAsync(request, manager, ct);
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
