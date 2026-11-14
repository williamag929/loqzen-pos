using System.Net.Http.Json;
using System.Net;
using System.Text.Json.Nodes;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// POST /api/comandas/{id}/pay-on-terminal end to end with the Mock driver and the REAL services (comanda, cash shift,
/// terminal, SQLite): approved → Order + Card payment + comanda Closed; declined / busy / no shift / closed / 400 / 404;
/// client abort of a hung charge; kitchen-event fan-out for the desktop comanda list.
/// Mock rules: total ending in 99 → Declined, 98 → hung (Timeout/Cancelled), anything else → Approved.
/// </summary>
public class PayOnTerminalEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    // ─── helpers ─────────────────────────────────────────────────────────────

    private Task<CashShift> OpenShiftAsync() =>
        _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero", 50_000m));

    private async Task<Guid> SeededMockTerminalIdAsync()
    {
        var list = (await _fx.GetJsonAsync("/api/payment-terminals")).AsArray();
        return list.Single(t => t!["name"]!.GetValue<string>() == "Datáfono simulado")!["id"]!.GetValue<Guid>();
    }

    private async Task<Guid> SeededProductIdAsync(string name)
    {
        var products = (await _fx.GetJsonAsync("/api/catalog/products")).AsArray();
        return products.Single(p => p!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<Guid>();
    }

    /// <summary>A tax-free product priced so the comanda total (qty 1) ends in the digits the Mock driver reacts to.</summary>
    private Task<Guid> ProductPricedAsync(decimal price) => _fx.WithDbAsync(async db =>
    {
        var p = new Product { Sku = $"T-{price:0}", Name = $"Prueba {price:0}", Price = price, TaxId = null, IsActive = true };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    });

    private async Task<Guid> OpenComandaAsync(string table, string? waiter = "Ana")
    {
        var response = await _fx.PostJsonAsync("/api/comandas", new { tableName = table, waiterName = waiter });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await _fx.ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    private async Task AddItemAsync(Guid comandaId, Guid productId, decimal quantity = 1)
    {
        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId, quantity });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task SendAsync(Guid comandaId)
    {
        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/send", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["pendingCount"]!.GetValue<int>().Should().Be(0);
    }

    /// <summary>Open + one unit of <paramref name="productId"/> + sent to the kitchen (no pending items left).</summary>
    private async Task<Guid> SentComandaAsync(string table, Guid productId)
    {
        var id = await OpenComandaAsync(table);
        await AddItemAsync(id, productId);
        await SendAsync(id);
        return id;
    }

    private Task<HttpResponseMessage> PayAsync(Guid comandaId, Guid terminalId, string? waiterName = "Ana", CancellationToken ct = default) =>
        _fx.Client.PostAsJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { terminalId, waiterName }, ServerFixture.Json, ct);

    // ─── approved ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approved_ClosesComanda_RecordsCardPayment_AndCharge()
    {
        var shift = await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var productId = await SeededProductIdAsync("Hamburguesa Especial"); // 22000 + 19% = 26180 → approved
        var comandaId = await SentComandaAsync("Mesa 1", productId);

        var response = await PayAsync(comandaId, terminalId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["success"]!.GetValue<bool>().Should().BeTrue();

        var charge = body["charge"]!;
        charge["status"]!.GetValue<string>().Should().Be("Approved");
        charge["isApproved"]!.GetValue<bool>().Should().BeTrue();
        charge["amount"]!.GetValue<decimal>().Should().Be(26180m);
        charge["currency"]!.GetValue<string>().Should().Be("COP");
        charge["purpose"]!.GetValue<string>().Should().Be("Comanda");
        charge["comandaId"]!.GetValue<Guid>().Should().Be(comandaId);
        charge["terminalName"]!.GetValue<string>().Should().Be("Datáfono simulado");
        charge["requestedByName"]!.GetValue<string>().Should().Be("Ana");
        charge["authorizationCode"]!.GetValue<string>().Should().HaveLength(6);
        var referenceText = charge["referenceText"]!.GetValue<string>();
        referenceText.Should().StartWith("AUTH ").And.Contain("****");
        charge["voucherText"]!.GetValue<string>().Should().Contain("APROBADA");
        var chargeId = charge["id"]!.GetValue<Guid>();

        var payment = body["payment"]!;
        payment["success"]!.GetValue<bool>().Should().BeTrue();
        var orderId = payment["orderId"]!.GetValue<Guid>();
        orderId.Should().NotBeEmpty();
        payment["orderNumber"]!.GetValue<string>().Should().StartWith("FAC-");
        payment["changeAmount"]!.GetValue<decimal>().Should().Be(0m);
        charge["orderId"]!.GetValue<Guid>().Should().Be(orderId, "the charge is linked to the invoice once the comanda is closed");

        var comanda = body["comanda"]!;
        comanda["id"]!.GetValue<Guid>().Should().Be(comandaId);
        comanda["status"]!.GetValue<string>().Should().Be("Closed");
        comanda["orderId"]!.GetValue<Guid>().Should().Be(orderId);

        // Database: a Completed order on the open shift paid by Card with the terminal reference.
        await _fx.WithDbAsync(async db =>
        {
            var order = await db.Orders.Include(o => o.Payments).SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.Completed);
            order.CashShiftId.Should().Be(shift.Id);
            order.Total.Should().Be(26180m);
            var pay = order.Payments.Should().ContainSingle().Subject;
            pay.Method.Should().Be(PaymentMethod.Card);
            pay.Amount.Should().Be(26180m);
            pay.Reference.Should().Be(referenceText);
            pay.TerminalPaymentId.Should().Be(chargeId);

            var row = await db.TerminalPayments.SingleAsync(p => p.Id == chargeId);
            row.Status.Should().Be(TerminalPaymentStatus.Approved);
            row.OrderId.Should().Be(orderId);
            row.ComandaId.Should().Be(comandaId);
            row.CashShiftId.Should().Be(shift.Id);
            row.Purpose.Should().Be(TerminalPaymentPurpose.Comanda);

            (await db.Comandas.SingleAsync(c => c.Id == comandaId)).Status.Should().Be(ComandaStatus.Closed);
        });

        // Recent charges on the tablet.
        var charges = (await _fx.GetJsonAsync("/api/payment-terminals/charges")).AsArray();
        var listed = charges.Should().ContainSingle().Subject!;
        listed["id"]!.GetValue<Guid>().Should().Be(chargeId);
        listed["purpose"]!.GetValue<string>().Should().Be("Comanda");
        listed["orderId"]!.GetValue<Guid>().Should().Be(orderId);
        listed["invoiceReference"]!.GetValue<string>().Should().StartWith("Comanda #");

        // The table is free again: the comanda is no longer in the active list.
        var active = (await _fx.GetJsonAsync("/api/comandas")).AsArray();
        active.Should().NotContain(c => c!["id"]!.GetValue<Guid>() == comandaId);
    }

    [Fact]
    public async Task Approved_FromOpenComanda_WithPendingItems_SendsThemToKitchen()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var productId = await SeededProductIdAsync("Gaseosa 400ml"); // 5000 + 19% = 5950
        var comandaId = await OpenComandaAsync("Mesa 2");
        await AddItemAsync(comandaId, productId, 2); // still pending (never "sent")

        var response = await PayAsync(comandaId, terminalId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["success"]!.GetValue<bool>().Should().BeTrue();
        body["charge"]!["amount"]!.GetValue<decimal>().Should().Be(11900m);
        body["comanda"]!["status"]!.GetValue<string>().Should().Be("Closed");
        body["comanda"]!["pendingCount"]!.GetValue<int>().Should().Be(0);

        // The pending items were confirmed → a kitchen ticket exists for the bar.
        var tickets = (await _fx.GetJsonAsync("/api/kds/orders")).AsArray();
        tickets.Should().Contain(t => t!["station"]!.GetValue<string>() == "Barra");
    }

    [Fact]
    public async Task WaiterName_Missing_FallsBackToComandaWaiter()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await SentComandaAsync("Mesa 1", await SeededProductIdAsync("Croissant de Mantequilla"));

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { terminalId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["charge"]!["requestedByName"]!.GetValue<string>().Should().Be("Ana");
    }

    // ─── declined ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Declined_LeavesComandaReadyToPay_NoOrder()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await SentComandaAsync("Mesa 1", await ProductPricedAsync(1099m));

        var response = await PayAsync(comandaId, terminalId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a decline is a business outcome, not an error");
        var body = await _fx.ReadJsonAsync(response);
        body["success"]!.GetValue<bool>().Should().BeFalse();
        var charge = body["charge"]!;
        charge["status"]!.GetValue<string>().Should().Be("Declined");
        charge["statusLabel"]!.GetValue<string>().Should().Be("Rechazada");
        charge["amount"]!.GetValue<decimal>().Should().Be(1099m);
        charge["message"]!.GetValue<string>().Should().Contain("Fondos insuficientes");
        charge["referenceText"]!.GetValue<string>().Should().BeEmpty();
        charge["orderId"].Should().BeNull();
        body["payment"].Should().BeNull();
        body["comanda"]!["status"]!.GetValue<string>().Should().Be("ReadyToPay");

        await _fx.WithDbAsync(async db =>
        {
            (await db.Orders.CountAsync()).Should().Be(0);
            (await db.Payments.CountAsync()).Should().Be(0);
            var row = await db.TerminalPayments.SingleAsync();
            row.Status.Should().Be(TerminalPaymentStatus.Declined);
            row.ComandaId.Should().Be(comandaId);
            row.OrderId.Should().BeNull();
            (await db.Comandas.SingleAsync(c => c.Id == comandaId)).Status.Should().Be(ComandaStatus.ReadyToPay);
        });

        // The waiter can retry on the same terminal right away (no Pending row left behind).
        var retry = await PayAsync(comandaId, terminalId);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(retry))["charge"]!["status"]!.GetValue<string>().Should().Be("Declined");
    }

    // ─── validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NoOpenShift_Returns409_AbraTurno()
    {
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await SentComandaAsync("Mesa 1", await SeededProductIdAsync("Gaseosa 400ml"));

        var response = await PayAsync(comandaId, terminalId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("Abra turno");
        await _fx.WithDbAsync(async db => (await db.TerminalPayments.CountAsync()).Should().Be(0, "nothing was sent to the terminal"));
    }

    [Fact]
    public async Task ClosedComanda_Returns409()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await SentComandaAsync("Mesa 1", await SeededProductIdAsync("Gaseosa 400ml"));
        (await PayAsync(comandaId, terminalId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await PayAsync(comandaId, terminalId);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(again))["error"]!.GetValue<string>().Should().Contain("cerrada");
        await _fx.WithDbAsync(async db => (await db.TerminalPayments.CountAsync()).Should().Be(1, "the second attempt never reached the terminal"));
    }

    [Fact]
    public async Task EmptyComanda_Returns409()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await OpenComandaAsync("Mesa 1");

        var response = await PayAsync(comandaId, terminalId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("no tiene productos");
    }

    [Fact]
    public async Task MissingTerminalId_Returns400()
    {
        await OpenShiftAsync();
        var comandaId = await SentComandaAsync("Mesa 1", await SeededProductIdAsync("Gaseosa 400ml"));

        var noBody = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", null);
        noBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(noBody))["error"]!.GetValue<string>().Should().Contain("datáfono");

        var emptyGuid = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { terminalId = Guid.Empty, waiterName = "Ana" });
        emptyGuid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var onlyWaiter = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/pay-on-terminal", new { waiterName = "Ana" });
        onlyWaiter.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownComanda_Returns404()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();

        var response = await PayAsync(Guid.NewGuid(), terminalId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Be("Comanda no encontrada.");
    }

    [Fact]
    public async Task UnknownTerminal_Returns404()
    {
        await OpenShiftAsync();
        var comandaId = await SentComandaAsync("Mesa 1", await SeededProductIdAsync("Gaseosa 400ml"));

        var response = await PayAsync(comandaId, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Be("Datáfono no encontrado.");
    }

    // ─── busy terminal + client abort ────────────────────────────────────────

    [Fact]
    public async Task Busy_SecondChargeOnSameTerminal_Returns409_AndAbortedFirstEndsCancelledOrTimeout()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var hungComanda = await SentComandaAsync("Mesa 1", await ProductPricedAsync(1098m)); // …98 → the terminal never answers
        var otherComanda = await SentComandaAsync("Mesa 2", await SeededProductIdAsync("Gaseosa 400ml"));

        using var abort = new CancellationTokenSource();
        var first = PayAsync(hungComanda, terminalId, "Ana", abort.Token);

        // Wait until the charge reached the (hung) device: by then the Pending audit row is committed.
        (await _fx.MockDriver.WaitForChargeStartAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("the first charge must reach the terminal");
        first.IsCompleted.Should().BeFalse("the hung charge is still waiting");
        (await _fx.WithDbAsync(db => db.TerminalPayments.SingleAsync(p => p.ComandaId == hungComanda))).Status.Should().Be(TerminalPaymentStatus.Pending);

        var second = await PayAsync(otherComanda, terminalId, "Luis");
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(second))["error"]!.GetValue<string>().Should().Contain("ocupado");
        await _fx.WithDbAsync(async db =>
        {
            (await db.TerminalPayments.CountAsync(p => p.ComandaId == otherComanda)).Should().Be(0, "a busy terminal must not get a second row");
            (await db.Comandas.SingleAsync(c => c.Id == otherComanda)).Status.Should().Be(ComandaStatus.Open, "a busy terminal must not touch the other table");
        });

        // The tablet taps "Cancelar": HttpClient aborts → Kestrel's RequestAborted → the charge is cancelled
        // (or, if the abort is not observed in time, the 5 s charge timeout ends it as Timeout).
        abort.Cancel();
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();

        (await _fx.MockDriver.WaitForChargeEndAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("the driver must give up on abort (or on the 5 s charge timeout)");
        var settled = await _fx.WaitForDbAsync(db => db.TerminalPayments.AnyAsync(p => p.ComandaId == hungComanda && p.Status != TerminalPaymentStatus.Pending), TimeSpan.FromSeconds(10));
        settled.Should().BeTrue("an aborted charge must not stay Pending");

        await _fx.WithDbAsync(async db =>
        {
            var row = await db.TerminalPayments.SingleAsync(p => p.ComandaId == hungComanda);
            row.Status.Should().BeOneOf(TerminalPaymentStatus.Cancelled, TerminalPaymentStatus.Timeout);
            row.CompletedAt.Should().NotBeNull();
            row.OrderId.Should().BeNull();
            (await db.Orders.CountAsync()).Should().Be(0);
            (await db.Comandas.SingleAsync(c => c.Id == hungComanda)).Status.Should().Be(ComandaStatus.ReadyToPay);
        });

        // The terminal is free again: the other table can now be charged.
        var retry = await PayAsync(otherComanda, terminalId, "Luis");
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(retry))["success"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Busy_DifferentTerminals_DoNotBlockEachOther()
    {
        await OpenShiftAsync();
        var mockId = await SeededMockTerminalIdAsync();
        var secondTerminal = await _fx.WithServiceAsync<IPaymentTerminalService, Core.Application.DTOs.PaymentTerminalDto>(s =>
            s.SaveAsync(new Core.Application.DTOs.SavePaymentTerminalRequest(null, "Datáfono 2", PaymentTerminalProvider.Mock, null, null, true, 1)));
        var hungComanda = await SentComandaAsync("Mesa 1", await ProductPricedAsync(1098m));
        var otherComanda = await SentComandaAsync("Mesa 2", await SeededProductIdAsync("Gaseosa 400ml"));

        using var abort = new CancellationTokenSource();
        var first = PayAsync(hungComanda, mockId, "Ana", abort.Token);
        (await _fx.MockDriver.WaitForChargeStartAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        var other = await PayAsync(otherComanda, secondTerminal.Id, "Luis");

        other.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(other))["success"]!.GetValue<bool>().Should().BeTrue();

        abort.Cancel();
        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();
        (await _fx.MockDriver.WaitForChargeEndAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await _fx.WaitForDbAsync(db => db.TerminalPayments.AnyAsync(p => p.ComandaId == hungComanda && p.Status != TerminalPaymentStatus.Pending), TimeSpan.FromSeconds(10))).Should().BeTrue();
    }

    // ─── kitchen events (desktop comanda list refresh) ───────────────────────

    [Fact]
    public async Task Approved_NoPendingItems_PublishesExactlyOneSyntheticRefreshEvent()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await SentComandaAsync("Mesa 4", await SeededProductIdAsync("Gaseosa 400ml"));

        var events = new List<KitchenEvent>();
        using var sub = _fx.Bus.Subscribe(e => { if (e.Type == KitchenEventType.ComandaChanged) return; lock (events) events.Add(e); }); // ticket events only

        var response = await PayAsync(comandaId, terminalId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var orderNumber = (await _fx.ReadJsonAsync(response))["payment"]!["orderNumber"]!.GetValue<string>();

        KitchenEvent e;
        lock (events) e = events.Should().ContainSingle("nothing was pending, so the endpoint publishes the synthetic refresh once").Subject;
        e.Type.Should().Be(KitchenEventType.Created);
        e.KitchenOrderId.Should().Be(Guid.Empty);
        e.Station.Should().BeEmpty();
        e.TableNumber.Should().Be("Mesa 4");
        e.OrderNumber.Should().Be(orderNumber);
        e.Status.Should().Be(KitchenOrderStatus.Delivered);
        e.OccurredAt.Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Approved_WithPendingItems_PublishesTicketEvents_NotTheSyntheticOne()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await OpenComandaAsync("Mesa 5");
        await AddItemAsync(comandaId, await SeededProductIdAsync("Gaseosa 400ml"));        // Barra
        await AddItemAsync(comandaId, await SeededProductIdAsync("Hamburguesa Especial")); // Cocina Caliente

        var events = new List<KitchenEvent>();
        using var sub = _fx.Bus.Subscribe(e => { if (e.Type == KitchenEventType.ComandaChanged) return; lock (events) events.Add(e); }); // ticket events only

        var response = await PayAsync(comandaId, terminalId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["success"]!.GetValue<bool>().Should().BeTrue();

        List<KitchenEvent> snapshot;
        lock (events) snapshot = events.ToList();
        snapshot.Should().HaveCount(2, "one real ticket per station was created by the service; no synthetic duplicate");
        snapshot.Should().OnlyContain(e => e.Type == KitchenEventType.Created && e.KitchenOrderId != Guid.Empty);
        snapshot.Select(e => e.Station).Should().BeEquivalentTo(new[] { "Barra", "Cocina Caliente" });
        snapshot.Should().OnlyContain(e => e.TableNumber == "Mesa 5");
    }

    [Fact]
    public async Task Declined_PublishesNoSyntheticEvent()
    {
        await OpenShiftAsync();
        var terminalId = await SeededMockTerminalIdAsync();
        var comandaId = await SentComandaAsync("Mesa 6", await ProductPricedAsync(1099m));

        var events = new List<KitchenEvent>();
        using var sub = _fx.Bus.Subscribe(e => { if (e.Type == KitchenEventType.ComandaChanged) return; lock (events) events.Add(e); }); // ticket events only

        var response = await PayAsync(comandaId, terminalId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["success"]!.GetValue<bool>().Should().BeFalse();

        lock (events) events.Should().BeEmpty("the table is still open; nothing changed for the desktop list");
    }
}
