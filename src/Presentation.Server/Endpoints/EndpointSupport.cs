using System.Security.Cryptography;
using System.Text;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Microsoft.Extensions.Options;
using Presentation.Server.Dtos;

namespace Presentation.Server.Endpoints;

/// <summary>
/// Shared plumbing for the minimal-API endpoints: one DI scope of the DESKTOP container per request
/// (application services are transient and DbContext-backed) and a uniform exception → status mapping.
/// </summary>
public static class EndpointSupport
{
    /// <summary>Resolves <typeparamref name="TService"/> in a fresh scope and runs <paramref name="action"/>.</summary>
    public static async Task<IResult> WithScopedAsync<TService>(
        this IServiceProvider appServices,
        Func<TService, Task<IResult>> action) where TService : notnull
    {
        using var scope = appServices.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        try
        {
            return await action(service).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    /// <summary>
    /// 400 for argument problems, 404 for a missing key, 409 for a business-rule violation.
    /// Anything else is a 500 with a generic message (details go to the error logger).
    /// </summary>
    public static IResult MapError(Exception ex) => ex switch
    {
        ArgumentException ae => Error(StatusCodes.Status400BadRequest, ae.Message),
        KeyNotFoundException ke => Error(StatusCodes.Status404NotFound, ke.Message),
        InvalidOperationException ioe => Error(StatusCodes.Status409Conflict, ioe.Message),
        OperationCanceledException => Error(StatusCodes.Status499ClientClosedRequest, "Solicitud cancelada."),
        _ => Error(StatusCodes.Status500InternalServerError, "Error interno del servidor.")
    };

    public static IResult Error(int statusCode, string message) =>
        Results.Json(new ErrorDto(message), ServerJson.Options, statusCode: statusCode);

    public static IResult NotFound(string message) => Error(StatusCodes.Status404NotFound, message);

    public static IResult Unauthorized() => Error(StatusCodes.Status401Unauthorized, "No autorizado.");

    /// <summary>
    /// Master/satellite routes (/api/sync/*, /api/terminals/*, /api/catalog/snapshot, /api/reports/*) require
    /// <c>Authorization: Bearer &lt;TerminalSettings.ApiKey&gt;</c> when an ApiKey is configured; with no key they
    /// stay open like the KDS/comandera routes. Per-endpoint filter on purpose: no global middleware.
    /// </summary>
    public static TBuilder RequireTerminalApiKey<TBuilder>(this TBuilder builder, IServiceProvider appServices)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new TerminalApiKeyFilter(appServices));

    /// <summary>TerminalSettings as registered by the desktop (plain singleton or IOptions), else defaults.</summary>
    public static TerminalSettings ResolveTerminalSettings(this IServiceProvider appServices) =>
        appServices.GetService<TerminalSettings>()
        ?? appServices.GetService<IOptions<TerminalSettings>>()?.Value
        ?? new TerminalSettings();

    /// <summary>KdsSettings as registered by the desktop (plain singleton or IOptions), else defaults.</summary>
    public static KdsSettings ResolveKdsSettings(this IServiceProvider appServices) =>
        appServices.GetService<KdsSettings>()
        ?? appServices.GetService<IOptions<KdsSettings>>()?.Value
        ?? new KdsSettings();

    public static ComandaSettings ResolveComandaSettings(this IServiceProvider appServices) =>
        appServices.GetService<ComandaSettings>()
        ?? appServices.GetService<IOptions<ComandaSettings>>()?.Value
        ?? new ComandaSettings();

    public static TimeProvider ResolveClock(this IServiceProvider appServices) =>
        appServices.GetService<TimeProvider>() ?? TimeProvider.System;
}

/// <summary>
/// Checks the Bearer token against <see cref="TerminalSettings.ApiKey"/> (resolved per request so a key set
/// through IOptions is honoured). Empty/absent key = open route. Comparison is constant-time.
/// </summary>
internal sealed class TerminalApiKeyFilter : IEndpointFilter
{
    private const string BearerPrefix = "Bearer ";
    private readonly IServiceProvider _appServices;

    public TerminalApiKeyFilter(IServiceProvider appServices) => _appServices = appServices;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = _appServices.ResolveTerminalSettings().ApiKey;
        if (string.IsNullOrWhiteSpace(expected))
            return next(context);

        return IsAuthorized(context.HttpContext, expected.Trim())
            ? next(context)
            : ValueTask.FromResult<object?>(EndpointSupport.Unauthorized());
    }

    private static bool IsAuthorized(HttpContext ctx, string expected)
    {
        string? header = ctx.Request.Headers.Authorization;
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = header[BearerPrefix.Length..].Trim();
        if (presented.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }
}
