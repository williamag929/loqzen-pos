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
/// Staff users with PIN login. PINs are stored as PBKDF2 hashes (<see cref="PinHasher"/>) and must be
/// unique among active users so PIN-only sign in is unambiguous.
/// </summary>
public class UserService : IUserService
{
    private const int MaxNameLength = 100;
    private const string PinInUseMessage = "Ese PIN ya está en uso por otro usuario.";

    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;

    public UserService(PosDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<UserDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var query = _db.Users.AsNoTracking();
        if (!includeInactive) query = query.Where(u => u.IsActive);
        var users = await query.OrderByDescending(u => u.Role).ThenBy(u => u.Name).ToListAsync(ct);
        return users.Select(MapToDto).ToList();
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user == null ? null : MapToDto(user);
    }

    public Task<bool> AnyActiveAsync(CancellationToken ct = default) =>
        _db.Users.AnyAsync(u => u.IsActive, ct);

    public async Task<UserDto> CreateAsync(string name, string pin, UserRole role, CancellationToken ct = default)
    {
        var cleanName = ValidateName(name);
        ValidatePin(pin);
        await EnsurePinNotInUseAsync(pin, excludeUserId: null, ct);

        var user = new User
        {
            Name = cleanName,
            PinHash = PinHasher.Hash(pin),
            Role = role,
            IsActive = true,
            CreatedAt = UtcNow,
            UpdatedAt = UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return MapToDto(user);
    }

    public async Task<UserDto> UpdateAsync(Guid id, string name, UserRole role, bool isActive, CancellationToken ct = default)
    {
        var cleanName = ValidateName(name);
        var user = await RequireAsync(id, ct);

        // Would this change remove the last active user able to authorize? That would lock everyone out.
        var losesAuthorizer = UserPermissions.CanAuthorize(user.Role) && user.IsActive
                              && (!isActive || !UserPermissions.CanAuthorize(role));
        if (losesAuthorizer)
        {
            var otherAuthorizers = await _db.Users
                .Where(u => u.Id != id && u.IsActive && u.Role >= UserRole.Manager)
                .AnyAsync(ct);
            if (!otherAuthorizers)
                throw new InvalidOperationException("No se puede desactivar o degradar al último usuario con permiso para autorizar.");
        }

        user.Name = cleanName;
        user.Role = role;
        user.IsActive = isActive;
        user.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return MapToDto(user);
    }

    public async Task ChangePinAsync(Guid id, string newPin, CancellationToken ct = default)
    {
        ValidatePin(newPin);
        var user = await RequireAsync(id, ct);
        await EnsurePinNotInUseAsync(newPin, excludeUserId: id, ct);

        user.PinHash = PinHasher.Hash(newPin);
        user.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<UserDto?> AuthenticateByPinAsync(string pin, CancellationToken ct = default)
    {
        if (!PinHasher.IsValidPin(pin)) return null;

        var active = await _db.Users.Where(u => u.IsActive).ToListAsync(ct);
        var user = active.FirstOrDefault(u => PinHasher.Verify(pin, u.PinHash));
        if (user == null) return null;

        user.LastLoginAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        return MapToDto(user);
    }

    // ---- helpers ----

    private static string ValidateName(string? name)
    {
        var clean = name?.Trim() ?? string.Empty;
        if (clean.Length == 0)
            throw new ArgumentException("El nombre es obligatorio.", nameof(name));
        if (clean.Length > MaxNameLength)
            throw new ArgumentException($"El nombre no puede superar {MaxNameLength} caracteres.", nameof(name));
        return clean;
    }

    private static void ValidatePin(string? pin)
    {
        if (!PinHasher.IsValidPin(pin))
            throw new ArgumentException($"El PIN debe tener entre {PinHasher.MinLength} y {PinHasher.MaxLength} dígitos.", nameof(pin));
    }

    private async Task EnsurePinNotInUseAsync(string pin, Guid? excludeUserId, CancellationToken ct)
    {
        var active = await _db.Users.AsNoTracking()
            .Where(u => u.IsActive && (excludeUserId == null || u.Id != excludeUserId))
            .Select(u => u.PinHash)
            .ToListAsync(ct);
        if (active.Any(hash => PinHasher.Verify(pin, hash)))
            throw new InvalidOperationException(PinInUseMessage);
    }

    private async Task<User> RequireAsync(Guid id, CancellationToken ct) =>
        await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
        ?? throw new InvalidOperationException("Usuario no encontrado.");

    private static UserDto MapToDto(User u) => new()
    {
        Id = u.Id,
        Name = u.Name,
        Role = u.Role,
        IsActive = u.IsActive,
        LastLoginAt = u.LastLoginAt,
        CreatedAt = u.CreatedAt
    };
}
