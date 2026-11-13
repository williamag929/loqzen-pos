using Core.Application.DTOs;
using Core.Application.Services;

namespace Presentation.Server.Endpoints;

/// <summary>
/// Registers known to this database (/api/terminals) and the satellite heartbeat (/api/terminals/heartbeat).
/// Behind the terminal API key when one is configured.
/// </summary>
public static class TerminalEndpoints
{
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app, IServiceProvider appServices)
    {
        var group = app.MapGroup("/api/terminals").RequireTerminalApiKey(appServices);

        // GET /api/terminals → TerminalDto[] (master first, then by code; open-shift columns filled).
        group.MapGet("", () => appServices.WithScopedAsync<ITerminalService>(async terminals =>
            Results.Ok(await terminals.GetAllAsync())));

        // POST /api/terminals/heartbeat (body: TerminalHeartbeatRequest) → 200 TerminalDto; 409 own code / Master kind.
        group.MapPost("/heartbeat", (HttpContext ctx, TerminalHeartbeatRequest? body) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Code))
                return Task.FromResult(EndpointSupport.Error(StatusCodes.Status400BadRequest, "El código de caja es obligatorio."));

            var remoteIp = ctx.Connection.RemoteIpAddress?.ToString();
            return appServices.WithScopedAsync<ITerminalService>(async terminals =>
                Results.Ok(await terminals.HeartbeatAsync(body, remoteIp, ctx.RequestAborted)));
        });

        return app;
    }
}
