using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Presentation.Desktop.Services;

/// <summary>Result of asking for manager approval. <see cref="Override"/> is null when no approval was needed.</summary>
public sealed record AuthorizationOutcome(bool IsAllowed, ManagerOverrideDto? Override)
{
    public string? ManagerName => Override?.ManagerName;
    public static readonly AuthorizationOutcome Denied = new(false, null);
    public static readonly AuthorizationOutcome NotRequired = new(true, null);
}

/// <summary>
/// UI-side gate for sensitive actions. Not required by config → allowed at once; signed-in manager →
/// recorded silently; otherwise a PIN dialog is shown and the call waits for the answer.
/// </summary>
public interface IManagerAuthPrompt
{
    Task<AuthorizationOutcome> RequestAsync(AuthorizedAction action, string description, decimal? amount = null, string? reference = null);
}
