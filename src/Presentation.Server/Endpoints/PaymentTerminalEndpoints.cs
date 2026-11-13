using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.Options;
using Presentation.Server.Dtos;

namespace Presentation.Server.Endpoints;

/// <summary>
/// Pay-at-table with wireless card terminals (datáfonos) for the comandera tablet:
/// <list type="bullet">
/// <item><c>GET /api/payment-terminals</c> → active terminals.</item>
/// <item><c>POST /api/payment-terminals/{id}/assign { userName }</c> → hand the terminal to a waiter (empty = back to the register).</item>
/// <item><c>POST /api/comandas/{id}/pay-on-terminal { terminalId, waiterName }</c> → charge the comanda total on the terminal and close it.</item>
/// <item><c>GET /api/payment-terminals/charges?take=20</c> → recent charges.</item>
/// </list>
/// Every route answers 404 <c>{ error: "Datáfonos desactivados." }</c> when <see cref="PaymentTerminalSettings.Enabled"/>
/// is false or the desktop did not register <see cref="IPaymentTerminalService"/> (the tablet hides the feature on 404).
/// <para>
/// pay-on-terminal is a LONG request (the cashier/waiter waits for the card: up to
/// <see cref="PaymentTerminalSettings.ChargeTimeoutSeconds"/> + a few seconds). Kestrel has no per-request timeout
/// (KeepAliveTimeout only applies between requests, RequestHeadersTimeout only to the headers), so nothing needs to
/// be raised in <c>LoqzenServer</c>. <c>HttpContext.RequestAborted</c> is passed to the service so closing the tab or
/// tapping "Cancelar" (AbortController) aborts the charge on the terminal.
/// </para>
/// </summary>
public static class PaymentTerminalEndpoints
{
    private const string DisabledMessage = "Datáfonos desactivados.";
    private const int DefaultChargesTake = 20;
    private const int MaxChargesTake = 200;

    public static IEndpointRouteBuilder MapPaymentTerminalEndpoints(this IEndpointRouteBuilder app, IServiceProvider appServices)
    {
        var group = app.MapGroup("/api/payment-terminals");

        group.MapGet("", () => appServices.WithTerminalsAsync(async terminals =>
        {
            var list = await terminals.GetTerminalsAsync(includeInactive: false);
            return Results.Ok(list.Select(t => t.ToDto()).ToList());
        }));

        // Static segment before "/{id:guid}" on purpose (the guid constraint keeps them apart anyway).
        group.MapGet("/charges", (int? take) => appServices.WithTerminalsAsync(async terminals =>
        {
            var n = Math.Clamp(take ?? DefaultChargesTake, 1, MaxChargesTake);
            var list = await terminals.GetRecentChargesAsync(n);
            return Results.Ok(list);
        }));

        group.MapPost("/{id:guid}/assign", (Guid id, AssignTerminalRequest? body) => appServices.WithTerminalsAsync(async terminals =>
        {
            if (await terminals.GetByIdAsync(id) is null)
                return EndpointSupport.NotFound("Datáfono no encontrado.");
            var userName = string.IsNullOrWhiteSpace(body?.UserName) ? null : body!.UserName!.Trim();
            var dto = await terminals.AssignAsync(id, null, userName);
            return Results.Ok(dto.ToDto());
        }));

        app.MapPost("/api/comandas/{id:guid}/pay-on-terminal", (Guid id, PayOnTerminalRequest? body, HttpContext ctx) =>
            appServices.WithTerminalsAsync(async (terminals, scope) =>
            {
                if (body is null || body.TerminalId == Guid.Empty)
                    return EndpointSupport.Error(StatusCodes.Status400BadRequest, "El datáfono es obligatorio.");

                var comandas = scope.GetRequiredService<IComandaService>();
                var before = await comandas.GetByIdAsync(id);
                if (before is null)
                    return EndpointSupport.NotFound("Comanda no encontrada.");
                if (await terminals.GetByIdAsync(body.TerminalId) is null)
                    return EndpointSupport.NotFound("Datáfono no encontrado.");

                var waiter = string.IsNullOrWhiteSpace(body.WaiterName) ? (string.IsNullOrWhiteSpace(before.WaiterName) ? "Mesero" : before.WaiterName) : body.WaiterName.Trim();

                // RequestAborted: the tablet's "Cancelar" (AbortController) or a closed tab cancels the charge.
                var result = await terminals.PayComandaOnTerminalAsync(id, body.TerminalId, null, waiter, progress: null, ct: ctx.RequestAborted);

                var after = await comandas.GetByIdAsync(id) ?? before;

                // ComandaService.CloseAndPayAsync only publishes KitchenEvents for tickets it creates (pending items
                // confirmed on close). When nothing was pending the desktop's comanda list would not learn that the
                // table was paid from the tablet, so we publish one refresh event ourselves in that case only.
                if (result.Success && before.PendingCount == 0)
                    NotifyComandaClosed(appServices, after, result.Payment);

                return Results.Ok(new PayOnTerminalResponseDto(result.Success, result.Charge, result.Payment, after));
            }));

        return app;
    }

    /// <summary>
    /// Desktop <c>ComandaViewModel</c> refreshes its list on <see cref="KitchenEventType.Created"/> only (and the KDS
    /// tablets / comandera tablets reload on any frame). A synthetic ticket event with an empty station is the
    /// only signal on the existing bus that reaches all of them; it is harmless for the KDS (it just reloads).
    /// </summary>
    private static void NotifyComandaClosed(IServiceProvider appServices, ComandaDto comanda, PaymentResultDto? payment)
    {
        var bus = appServices.GetService<IKitchenEventBus>();
        if (bus is null) return;
        var now = appServices.ResolveClock().GetUtcNow().UtcDateTime;
        bus.Publish(new KitchenEvent(
            KitchenEventType.Created,
            Guid.Empty,
            string.Empty,
            payment?.OrderNumber ?? comanda.OrderNumber ?? $"#{comanda.Number}",
            comanda.TableName,
            KitchenOrderStatus.Delivered,
            now));
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────

    /// <summary>PaymentTerminalSettings as registered by the desktop (plain singleton or IOptions), else defaults.</summary>
    public static PaymentTerminalSettings ResolvePaymentTerminalSettings(this IServiceProvider appServices) =>
        appServices.GetService<PaymentTerminalSettings>()
        ?? appServices.GetService<IOptions<PaymentTerminalSettings>>()?.Value
        ?? new PaymentTerminalSettings();

    private static Task<IResult> WithTerminalsAsync(this IServiceProvider appServices, Func<IPaymentTerminalService, Task<IResult>> action) =>
        appServices.WithTerminalsAsync((terminals, _) => action(terminals));

    /// <summary>
    /// Like <see cref="EndpointSupport.WithScopedAsync{TService}"/> but answers 404 "Datáfonos desactivados." when the
    /// feature is off or the service is not registered, and hands the scope over for sibling services.
    /// </summary>
    private static async Task<IResult> WithTerminalsAsync(this IServiceProvider appServices, Func<IPaymentTerminalService, IServiceProvider, Task<IResult>> action)
    {
        if (!appServices.ResolvePaymentTerminalSettings().Enabled)
            return EndpointSupport.NotFound(DisabledMessage);

        using var scope = appServices.CreateScope();
        var terminals = scope.ServiceProvider.GetService<IPaymentTerminalService>();
        if (terminals is null)
            return EndpointSupport.NotFound(DisabledMessage);
        try
        {
            return await action(terminals, scope.ServiceProvider).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return EndpointSupport.MapError(ex);
        }
    }
}
