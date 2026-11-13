using System.Globalization;
using System.Text.Json;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Enums;
using Presentation.Server.Dtos;

namespace Presentation.Server.Endpoints;

/// <summary>
/// Master side of the second-register feature: the Outbox inbox (/api/sync/inbox) fed by the satellites'
/// <c>HttpOutboxSink</c>, and the consolidated day report (/api/reports/day). Both behind the terminal API key.
/// </summary>
public static class SyncEndpoints
{
    private const string DayFormat = "yyyy-MM-dd";

    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app, IServiceProvider appServices)
    {
        var sync = app.MapGroup("/api/sync").RequireTerminalApiKey(appServices);

        // POST /api/sync/inbox (body: SyncEnvelopeDto) → 200 { outcome, error: null } | 422 { outcome: "Rejected", error } | 400 malformed JSON.
        sync.MapPost("/inbox", async (HttpContext ctx) =>
        {
            SyncEnvelopeDto? envelope;
            try
            {
                // Bound by hand so a malformed body answers with the usual ErrorDto instead of an empty 400.
                envelope = await JsonSerializer.DeserializeAsync<SyncEnvelopeDto>(ctx.Request.Body, ServerJson.Options, ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return EndpointSupport.Error(StatusCodes.Status400BadRequest, "Cuerpo JSON inválido.");
            }
            catch (OperationCanceledException ex)
            {
                return EndpointSupport.MapError(ex);
            }

            if (envelope is null || envelope.Id == Guid.Empty || string.IsNullOrWhiteSpace(envelope.EventType))
                return EndpointSupport.Error(StatusCodes.Status400BadRequest, "El mensaje de sincronización es inválido (id y eventType son obligatorios).");

            return await appServices.WithScopedAsync<ISyncInboxService>(async inbox =>
            {
                var result = await inbox.ApplyAsync(envelope, ctx.RequestAborted);
                var body = new SyncInboxResponseDto(result.Outcome, result.Error);
                // Non-2xx makes the satellite's sink count a failed attempt and retry with backoff.
                return result.IsAcknowledged
                    ? Results.Json(body, ServerJson.Options)
                    : Results.Json(body, ServerJson.Options, statusCode: StatusCodes.Status422UnprocessableEntity);
            });
        });

        var reports = app.MapGroup("/api/reports").RequireTerminalApiKey(appServices);

        // GET /api/reports/day?date=YYYY-MM-DD (default: today in the server's local zone) → DayConsolidationDto.
        reports.MapGet("/day", (string? date) =>
        {
            DateOnly day;
            if (string.IsNullOrWhiteSpace(date))
            {
                day = DateOnly.FromDateTime(appServices.ResolveClock().GetLocalNow().DateTime);
            }
            else if (!DateOnly.TryParseExact(date.Trim(), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            {
                return Task.FromResult(EndpointSupport.Error(StatusCodes.Status400BadRequest, "Fecha inválida: use el formato AAAA-MM-DD."));
            }

            // DayConsolidationDto's totals are read-only getters; System.Text.Json serializes them like any property.
            return appServices.WithScopedAsync<ICashShiftService>(async shifts =>
                Results.Ok(await shifts.GetDayConsolidationAsync(day)));
        });

        return app;
    }
}
