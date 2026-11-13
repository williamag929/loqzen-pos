using System;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Desktop.Services;

/// <summary>Result of <see cref="RequiredOverride.RequestAsync"/>: the override to pass to the service, or a message for the cashier.</summary>
public sealed record RequiredOverrideResult(ManagerOverrideDto? Override, string? Error)
{
    public bool IsApproved => Override != null;
}

/// <summary>
/// For actions whose SERVICE requires a ManagerOverride id (cash movements, cash difference, bill-payment void):
/// asks through the shared prompt; when the action is not configured as "manager required" the prompt returns
/// no override, so a signed-in manager records one silently (same rule as ComandaViewModel.CancelConfirmedItem).
/// </summary>
public static class RequiredOverride
{
    public static async Task<RequiredOverrideResult> RequestAsync(
        IManagerAuthPrompt prompt, IServiceScopeFactory scopes, IUserSession session,
        AuthorizedAction action, string description, decimal? amount = null, string? reference = null)
    {
        AuthorizationOutcome auth;
        try
        {
            auth = await prompt.RequestAsync(action, description, amount, reference);
        }
        catch (Exception ex)
        {
            return new(null, $"❌ Error de autorización: {ex.Message}");
        }

        if (!auth.IsAllowed) return new(null, "❌ No autorizado");
        if (auth.Override != null) return new(auth.Override, null);

        var user = session.CurrentUser;
        if (user is not { CanAuthorize: true }) return new(null, "⚠️ Requiere gerente");
        try
        {
            using var scope = scopes.CreateScope();
            var ovr = await scope.ServiceProvider.GetRequiredService<IManagerAuthorizationService>()
                .AuthorizeAsUserAsync(new AuthorizationRequest(action, description, amount, reference, user.Id, user.Name), user.Id);
            return new(ovr, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, "⚠️ Requiere gerente");
        }
        catch (Exception ex)
        {
            return new(null, $"❌ Error: {ex.Message}");
        }
    }
}
