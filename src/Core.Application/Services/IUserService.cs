using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Core.Application.Services;

/// <summary>Staff users with PIN login. PINs are hashed; active users must have distinct PINs.</summary>
public interface IUserService
{
    Task<IReadOnlyList<UserDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> AnyActiveAsync(CancellationToken ct = default);

    /// <summary>Throws ArgumentException on invalid PIN/name, InvalidOperationException if the PIN is already in use.</summary>
    Task<UserDto> CreateAsync(string name, string pin, UserRole role, CancellationToken ct = default);

    Task<UserDto> UpdateAsync(Guid id, string name, UserRole role, bool isActive, CancellationToken ct = default);

    /// <summary>Throws InvalidOperationException if another active user already has this PIN.</summary>
    Task ChangePinAsync(Guid id, string newPin, CancellationToken ct = default);

    /// <summary>PIN-only sign in. Returns null when no active user matches. Records LastLoginAt.</summary>
    Task<UserDto?> AuthenticateByPinAsync(string pin, CancellationToken ct = default);
}
