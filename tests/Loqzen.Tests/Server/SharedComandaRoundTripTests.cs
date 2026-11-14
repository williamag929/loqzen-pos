using System.Globalization;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Application.Sync;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// Shared comandas between registers, end to end over real HTTP: the satellite (CAJA-02, own SQLite) drives the master's
/// comandas and kitchen through <c>RemoteComandaService</c> / <c>RemoteKitchenService</c>, invoices on ITS OWN database,
/// and the master learns about it twice (direct close-remote + Outbox ComandaClosedRemotely/OrderCompleted via the inbox).
/// </summary>
public class SharedComandaRoundTripTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();
    private SatelliteContainer _sat = null!;

    public async Task InitializeAsync()
    {
        await _fx.InitializeAsync();
        _sat = await _fx.CreateSatelliteAsync();
        await _sat.PullCatalogAsync(); // the satellite needs the master's product ids to invoice
    }

    public async Task DisposeAsync()
    {
        await _sat.DisposeAsync();
        await _fx.DisposeAsync();
    }

    private DateTime Now => _fx.Clock.GetUtcNow().UtcDateTime;
    private DateOnly ClockLocalDay => DateOnly.FromDateTime(_fx.Clock.GetLocalNow().DateTime);

    private Task<T> OnSatelliteComandas<T>(Func<IComandaService, Task<T>> work) => _sat.WithServiceAsync<IComandaService, T>(work);
    private Task<T> OnSatelliteKitchen<T>(Func<IKitchenService, Task<T>> work) => _sat.WithServiceAsync<IKitchenService, T>(work);

    private Task<Guid> MasterProductIdAsync(string sku) =>
        _fx.WithDbAsync(db => db.Products.Where(p => p.Sku == sku).Select(p => p.Id).SingleAsync());

    /// <summary>Satellite opens Mesa 5 for Luis, adds burger + 2 sodas (one with a note) and sends them to the kitchen.</summary>
    private async Task<ComandaDto> OpenMesa5WithItemsSentAsync()
    {
        var opened = await OnSatelliteComandas(c => c.OpenComandaAsync("Mesa 5", "Luis"));
        var burger = await MasterProductIdAsync("BUR-001");
        var soda = await MasterProductIdAsync("BEB-001");
        await OnSatelliteComandas(c => c.AddItemAsync(opened.Id, burger, 1m, null, "término medio"));
        await OnSatelliteComandas(c => c.AddItemAsync(opened.Id, soda, 2m));
        return await OnSatelliteComandas(c => c.ConfirmPendingNowAsync(opened.Id));
    }

    private Task<CashShift> OpenSatelliteShiftAsync(decimal opening = 50000m) =>
        _sat.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Satélite", opening, null));

    // ── (a) open from the satellite ──────────────────────────────────────────

    [Fact]
    public async Task Satellite_OpensAComanda_OnTheMaster()
    {
        var dto = await OnSatelliteComandas(c => c.OpenComandaAsync("Mesa 5", "Luis"));

        dto.TableName.Should().Be("Mesa 5");
        dto.WaiterName.Should().Be("Luis");
        dto.Status.Should().Be(ComandaStatus.Open);
        dto.OpenedAt.ToUniversalTime().Should().Be(Now);

        // It lives on the master only.
        var master = (await _fx.GetJsonAsync("/api/comandas")).AsArray().Should().ContainSingle().Which;
        master["id"]!.GetValue<Guid>().Should().Be(dto.Id);
        master["waiterName"]!.GetValue<string>().Should().Be("Luis");
        (await _fx.WithDbAsync(db => db.Comandas.CountAsync())).Should().Be(1);
        (await _sat.WithDbAsync(db => db.Comandas.CountAsync())).Should().Be(0, "a satellite never stores comandas");

        // The satellite's own list and lookups read the master.
        var active = await OnSatelliteComandas(c => c.GetActiveComandasAsync());
        active.Should().ContainSingle().Which.Id.Should().Be(dto.Id);
        (await OnSatelliteComandas(c => c.GetByIdAsync(dto.Id)))!.TableName.Should().Be("Mesa 5");
        (await OnSatelliteComandas(c => c.GetByIdAsync(Guid.NewGuid()))).Should().BeNull();
        var tables = await OnSatelliteComandas(c => c.GetTablesAsync());
        tables.Should().NotBeEmpty();
        tables.Single(t => t.Name == "Mesa 5").OpenComandaId.Should().BeNull("opened by name, not bound to the DiningTable row");

        // Same table from the master: the master's rule answers through the remote service as InvalidOperationException.
        var again = () => OnSatelliteComandas(c => c.OpenComandaAsync("Mesa 5", "Ana"));
        (await again.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Mesa 5*");
    }

    // ── (b) items + kitchen ──────────────────────────────────────────────────

    [Fact]
    public async Task Satellite_AddsItemsAndSends_KitchenTicketsLiveOnTheMaster_AndAreReadableAndAdvanceableRemotely()
    {
        var sent = await OpenMesa5WithItemsSentAsync();

        sent.Items.Should().HaveCount(2);
        sent.Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed);
        sent.Items.Single(i => i.ProductName == "Hamburguesa Especial").Notes.Should().Be("término medio");
        sent.Items.Single(i => i.ProductName == "Gaseosa 400ml").Quantity.Should().Be(2m);
        sent.PendingCount.Should().Be(0);
        sent.Total.Should().BeGreaterThan(0);

        // Master: one ticket per station, no tickets on the satellite.
        var masterTickets = await _fx.WithDbAsync(db => db.KitchenOrders.AsNoTracking().Include(k => k.Items).Where(k => k.TableNumber == "Mesa 5").ToListAsync());
        masterTickets.Select(k => k.Station).Should().BeEquivalentTo("Cocina Caliente", "Barra");
        masterTickets.Should().OnlyContain(k => k.OrderNumber == $"CMD-{sent.Number}" && k.WaiterName == "Luis");
        (await _sat.WithDbAsync(db => db.KitchenOrders.CountAsync())).Should().Be(0);

        // Satellite KDS reads them back with items (KitchenOrderWire → KitchenOrder).
        var remote = await OnSatelliteKitchen(k => k.GetActiveAsync());
        var mesa5 = remote.Where(k => k.TableNumber == "Mesa 5").ToList();
        mesa5.Should().HaveCount(2);
        var cocina = mesa5.Single(k => k.Station == "Cocina Caliente");
        cocina.Items.Should().ContainSingle();
        cocina.Items.Single().ProductName.Should().Be("Hamburguesa Especial");
        cocina.Items.Single().ProductId.Should().Be(await MasterProductIdAsync("BUR-001"));
        cocina.Items.Single().Notes.Should().Be("término medio");
        cocina.Items.Single().KitchenOrderId.Should().Be(cocina.Id);
        cocina.Status.Should().Be(KitchenOrderStatus.Pending);
        cocina.CreatedAt.ToUniversalTime().Should().Be(Now);
        var barra = mesa5.Single(k => k.Station == "Barra");
        barra.Items.Single().Quantity.Should().Be(2m);
        (await OnSatelliteKitchen(k => k.GetActiveAsync("barra"))).Should().OnlyContain(k => k.Station == "Barra");
        (await OnSatelliteKitchen(k => k.GetByIdAsync(cocina.Id)))!.Items.Should().ContainSingle();
        (await OnSatelliteKitchen(k => k.GetByIdAsync(Guid.NewGuid()))).Should().BeNull();

        // Advance from the satellite: the master's row moves.
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var advanced = await OnSatelliteKitchen(k => k.AdvanceAsync(cocina.Id));
        advanced.Status.Should().Be(KitchenOrderStatus.InPreparation);
        advanced.UpdatedAt!.Value.ToUniversalTime().Should().Be(Now);
        (await _fx.WithDbAsync(db => db.KitchenOrders.AsNoTracking().SingleAsync(k => k.Id == cocina.Id))).Status.Should().Be(KitchenOrderStatus.InPreparation);

        // Printed / undo rules are the master's too.
        (await OnSatelliteKitchen(k => k.MarkPrintedAsync(barra.Id))).PrintedAt.Should().NotBeNull();
        var undo = () => OnSatelliteKitchen(k => k.UndoDeliverAsync(barra.Id));
        await undo.Should().ThrowAsync<InvalidOperationException>("not delivered yet → 409 → InvalidOperationException");
        var create = () => OnSatelliteKitchen(k => k.CreateAsync(new KitchenOrder()));
        await create.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── (c) + (d) invoice on the satellite, replicate to the master ──────────

    [Fact]
    public async Task Satellite_LocksAndCloses_InvoicesLocally_MasterClosesWithoutAnOrder_ThenTheOutboxBringsTheOrder()
    {
        var comanda = await OpenMesa5WithItemsSentAsync();
        var total = comanda.Total;
        var shift = await OpenSatelliteShiftAsync();
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));

        // (c) Lock, then close + pay on the satellite.
        var locked = await OnSatelliteComandas(c => c.LockForPaymentAsync(comanda.Id, "CAJA-02"));
        locked.Status.Should().Be(ComandaStatus.ReadyToPay);
        locked.PaymentLockedByTerminalCode.Should().Be("CAJA-02");
        (await _fx.GetJsonAsync($"/api/comandas/{comanda.Id}"))["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-02");

        var pay = await OnSatelliteComandas(c => c.CloseAndPayAsync(comanda.Id, shift.Id, PaymentMethod.Cash, total + 10000m));

        pay.Success.Should().BeTrue(pay.ErrorMessage);
        pay.OrderId.Should().NotBeEmpty();
        pay.OrderNumber.Should().NotBeNullOrWhiteSpace();
        pay.ChangeAmount.Should().Be(10000m);

        // Local satellite order: Completed, items, payment, on the satellite's shift and register.
        var localOrder = await _sat.WithDbAsync(db => db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Payments).SingleAsync(o => o.Id == pay.OrderId));
        localOrder.Status.Should().Be(OrderStatus.Completed);
        localOrder.OrderNumber.Should().Be(pay.OrderNumber);
        localOrder.CashShiftId.Should().Be(shift.Id);
        localOrder.TerminalCode.Should().Be("CAJA-02");
        localOrder.TerminalId.Should().Be(_sat.Terminal.Id);
        localOrder.Total.Should().Be(total);
        localOrder.Items.Should().HaveCount(2);
        localOrder.Items.Select(i => i.ProductId).Should().BeEquivalentTo(comanda.Items.Select(i => i.ProductId));
        localOrder.Items.Single(i => i.ProductName == "Gaseosa 400ml").Quantity.Should().Be(2m);
        localOrder.Payments.Should().ContainSingle();
        localOrder.Payments.Single().Method.Should().Be(PaymentMethod.Cash);
        localOrder.Payments.Single().Amount.Should().Be(total);
        localOrder.Payments.Single().TenderedAmount.Should().Be(total + 10000m);
        localOrder.Payments.Single().ChangeAmount.Should().Be(10000m);

        // Local Outbox: OrderCompleted + ComandaClosedRemotely, both Pending (plus the shift opening).
        var outbox = await _sat.WithDbAsync(db => db.OutboxMessages.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync());
        outbox.Select(m => m.EventType).Should().BeEquivalentTo(SyncEventTypes.CashShiftOpened, SyncEventTypes.OrderCompleted, SyncEventTypes.ComandaClosedRemotely);
        outbox.Should().OnlyContain(m => m.Status == SyncStatus.Pending);
        var closedMsg = outbox.Single(m => m.EventType == SyncEventTypes.ComandaClosedRemotely);
        closedMsg.AggregateType.Should().Be("Comanda");
        closedMsg.AggregateId.Should().Be(comanda.Id);
        closedMsg.PayloadJson.Should().Contain(pay.OrderNumber).And.Contain("CAJA-02").And.Contain(pay.OrderId.ToString());
        outbox.Single(m => m.EventType == SyncEventTypes.OrderCompleted).AggregateId.Should().Be(pay.OrderId);

        // Master: Closed, paid by CAJA-02 with the remote invoice number, NO Order yet, gone from the active list.
        var masterDto = await _fx.GetJsonAsync($"/api/comandas/{comanda.Id}");
        masterDto["status"]!.GetValue<string>().Should().Be("Closed");
        masterDto["paidByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-02");
        masterDto["orderNumber"]!.GetValue<string>().Should().Be(pay.OrderNumber);
        masterDto["orderId"].Should().BeNull();
        masterDto["paymentLockedByTerminalCode"].Should().BeNull();
        // ComandaDto timestamps come back Kind=Unspecified after the DB round-trip (no "Z", unlike KitchenOrderWire): compare the value only.
        masterDto["closedAt"]!.GetValue<DateTime>().Should().Be(Now);
        var masterRow = await _fx.WithDbAsync(db => db.Comandas.AsNoTracking().SingleAsync(c => c.Id == comanda.Id));
        masterRow.RemoteOrderNumber.Should().Be(pay.OrderNumber);
        masterRow.RemoteOrderId.Should().Be(pay.OrderId);
        masterRow.OrderId.Should().BeNull();
        (await _fx.WithDbAsync(db => db.Orders.AnyAsync(o => o.Id == pay.OrderId))).Should().BeFalse("the Order arrives through the sync inbox, not through close-remote");
        (await _fx.GetJsonAsync("/api/comandas")).AsArray().Should().BeEmpty();
        (await OnSatelliteComandas(c => c.GetActiveComandasAsync())).Should().BeEmpty();
        (await _fx.WithDbAsync(db => db.SyncInboxReceipts.CountAsync())).Should().Be(0, "nothing dispatched yet");

        // (d) Outbox dispatch → master inbox.
        _fx.Clock.Advance(TimeSpan.FromSeconds(30));
        var dispatched = await _sat.DispatchAsync();
        dispatched.Should().Be(new OutboxDispatchResult(3, 0, 0));
        (await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Status != SyncStatus.Synced))).Should().Be(0);

        var receipts = await _fx.WithServiceAsync<ISyncInboxService, IReadOnlyList<SyncInboxReceiptDto>>(i => i.GetRecentAsync());
        receipts.Should().HaveCount(3);
        receipts.Should().OnlyContain(r => r.TerminalCode == "CAJA-02");
        receipts.Single(r => r.EventType == SyncEventTypes.ComandaClosedRemotely).Outcome.Should().Be(SyncInboxOutcome.Applied, "idempotent after the direct close");
        receipts.Single(r => r.EventType == SyncEventTypes.OrderCompleted).Outcome.Should().Be(SyncInboxOutcome.Applied);
        receipts.Single(r => r.EventType == SyncEventTypes.CashShiftOpened).Outcome.Should().Be(SyncInboxOutcome.Applied);

        // The master now holds the satellite's invoice, stamped CAJA-02, and the comanda is untouched (still Closed by CAJA-02).
        var masterSender = await _fx.WithDbAsync(db => db.Terminals.AsNoTracking().SingleAsync(t => t.Code == "CAJA-02"));
        var masterOrder = await _fx.WithDbAsync(db => db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Payments).SingleAsync(o => o.Id == pay.OrderId));
        masterOrder.TerminalCode.Should().Be("CAJA-02");
        masterOrder.TerminalId.Should().Be(masterSender.Id);
        masterOrder.CashShiftId.Should().Be(shift.Id);
        masterOrder.OrderNumber.Should().Be(pay.OrderNumber);
        masterOrder.Total.Should().Be(total);
        masterOrder.Items.Should().HaveCount(2);
        masterOrder.Payments.Should().ContainSingle().Which.Amount.Should().Be(total);
        var afterInbox = await _fx.WithDbAsync(db => db.Comandas.AsNoTracking().SingleAsync(c => c.Id == comanda.Id));
        afterInbox.Status.Should().Be(ComandaStatus.Closed);
        afterInbox.PaidByTerminalCode.Should().Be("CAJA-02");
        afterInbox.RemoteOrderId.Should().Be(pay.OrderId);
        afterInbox.OrderId.Should().BeNull("the comanda keeps pointing at the remote invoice; the master's own OrderId stays null");
        (await _fx.WithDbAsync(db => db.Comandas.CountAsync())).Should().Be(1);
        (await _fx.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(1, "nothing doubled");

        // Consolidated day: CAJA-02 cash sales = the comanda's total.
        var day = await _fx.GetJsonAsync($"/api/reports/day?date={ClockLocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        day["terminals"]!.AsArray().Select(t => t!["terminalCode"]!.GetValue<string>()).Should().Contain("CAJA-02", day.ToJsonString());
        var caja2 = day["terminals"]!.AsArray().Single(t => t!["terminalCode"]!.GetValue<string>() == "CAJA-02")!;
        caja2["orderCount"]!.GetValue<int>().Should().Be(1);
        caja2["cashSales"]!.GetValue<decimal>().Should().Be(total);
        caja2["totalSales"]!.GetValue<decimal>().Should().Be(total);
        caja2["openShiftCount"]!.GetValue<int>().Should().Be(1);
        day["cashSales"]!.GetValue<decimal>().Should().Be(total);
        day["orderCount"]!.GetValue<int>().Should().Be(1);
        day["terminals"]!.AsArray().Should().ContainSingle("the master opened no shift of its own: only CAJA-02 has a bucket");

        // Retry of the same messages: Duplicate, nothing doubles.
        await _sat.WithDbAsync(async db =>
        {
            foreach (var m in await db.OutboxMessages.ToListAsync()) { m.Status = SyncStatus.Pending; m.ProcessedAt = null; }
            await db.SaveChangesAsync();
        });
        (await _sat.DispatchAsync()).Should().Be(new OutboxDispatchResult(3, 0, 0));
        (await _fx.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(1);
        (await _fx.WithDbAsync(db => db.SyncInboxReceipts.CountAsync())).Should().Be(3);
    }

    [Fact]
    public async Task Satellite_CloseAndPay_WithoutAnOpenShift_FailsLocally_AndNeverTouchesTheMaster()
    {
        var comanda = await OpenMesa5WithItemsSentAsync();

        var result = await OnSatelliteComandas(c => c.CloseAndPayAsync(comanda.Id, Guid.NewGuid(), PaymentMethod.Cash, comanda.Total));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("turno");
        var master = await _fx.GetJsonAsync($"/api/comandas/{comanda.Id}");
        master["status"]!.GetValue<string>().Should().Be("Open", "no lock was even attempted");
        master["paymentLockedByTerminalCode"].Should().BeNull();
        (await _sat.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(0);
        (await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Satellite_CloseAndPay_InsufficientCash_ReleasesTheLock_AndCreatesNothing()
    {
        var comanda = await OpenMesa5WithItemsSentAsync();
        var shift = await OpenSatelliteShiftAsync();

        var result = await OnSatelliteComandas(c => c.CloseAndPayAsync(comanda.Id, shift.Id, PaymentMethod.Cash, comanda.Total - 1m));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("insuficiente");
        var master = await _fx.GetJsonAsync($"/api/comandas/{comanda.Id}");
        master["status"]!.GetValue<string>().Should().Be("ReadyToPay", "the lock moved it to ReadyToPay; the failure does not reopen it");
        master["paymentLockedByTerminalCode"].Should().BeNull("the lock was given back");
        (await _sat.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(0);
        (await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.EventType != SyncEventTypes.CashShiftOpened))).Should().Be(0);

        // The master itself can now collect it.
        var masterShift = await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Principal", 0m, null));
        var paidHere = await _fx.WithServiceAsync<IComandaService, PaymentResultDto>(c => c.CloseAndPayAsync(comanda.Id, masterShift.Id, PaymentMethod.Cash, comanda.Total));
        paidHere.Success.Should().BeTrue(paidHere.ErrorMessage);
    }

    // ── (e) conflict with the master ─────────────────────────────────────────

    [Fact]
    public async Task Satellite_CloseAndPay_WhileTheMasterIsCollecting_Throws409WithTheHolder_AndCreatesNothing()
    {
        var comanda = await OpenMesa5WithItemsSentAsync();
        var shift = await OpenSatelliteShiftAsync();
        // The master register starts collecting the table (what its pay panel does first).
        var masterLock = await _fx.WithServiceAsync<IComandaService, ComandaDto>(c => c.LockForPaymentAsync(comanda.Id, ServerFixture.MasterCode));
        masterLock.PaymentLockedByTerminalCode.Should().Be("CAJA-01");

        var act = () => OnSatelliteComandas(c => c.CloseAndPayAsync(comanda.Id, shift.Id, PaymentMethod.Cash, comanda.Total));

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*CAJA-01*");
        (await _sat.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(0);
        (await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.EventType != SyncEventTypes.CashShiftOpened))).Should().Be(0);
        var master = await _fx.GetJsonAsync($"/api/comandas/{comanda.Id}");
        master["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-01", "the satellite's failed attempt must not release the master's lock");
        master["status"]!.GetValue<string>().Should().Be("ReadyToPay");

        // Mirror image: the satellite holds the lock, the master's local CloseAndPayAsync is refused the same way.
        await _fx.WithServiceAsync<IComandaService>(c => c.UnlockPaymentAsync(comanda.Id, ServerFixture.MasterCode));
        await OnSatelliteComandas(c => c.LockForPaymentAsync(comanda.Id, "CAJA-02"));
        var masterShift = await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Principal", 0m, null));
        var masterPay = () => _fx.WithServiceAsync<IComandaService, PaymentResultDto>(c => c.CloseAndPayAsync(comanda.Id, masterShift.Id, PaymentMethod.Cash, comanda.Total));
        (await masterPay.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*CAJA-02*");
        (await _fx.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Satellite_CloseAndPay_OnAComandaAlreadyPaidByTheMaster_Throws_AndCreatesNothing()
    {
        var comanda = await OpenMesa5WithItemsSentAsync();
        var shift = await OpenSatelliteShiftAsync();
        var masterShift = await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Principal", 0m, null));
        (await _fx.WithServiceAsync<IComandaService, PaymentResultDto>(c => c.CloseAndPayAsync(comanda.Id, masterShift.Id, PaymentMethod.Cash, comanda.Total))).Success.Should().BeTrue();

        var act = () => OnSatelliteComandas(c => c.CloseAndPayAsync(comanda.Id, shift.Id, PaymentMethod.Cash, comanda.Total));

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*cerrada*");
        (await _sat.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(0);
        (await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.EventType != SyncEventTypes.CashShiftOpened))).Should().Be(0);
    }

    // ── (f) + (g) master offline ─────────────────────────────────────────────

    [Fact]
    public async Task MasterDown_RemoteReadsThrowSinConexion()
    {
        await OpenMesa5WithItemsSentAsync();
        await _fx.Server.StopAsync();
        _fx.Server.IsRunning.Should().BeFalse();

        var comandas = () => OnSatelliteComandas(c => c.GetActiveComandasAsync());
        (await comandas.Should().ThrowAsync<HttpRequestException>()).WithMessage("Sin conexión*");

        var tables = () => OnSatelliteComandas(c => c.GetTablesAsync());
        (await tables.Should().ThrowAsync<HttpRequestException>()).WithMessage("Sin conexión*");

        var byId = () => OnSatelliteComandas(c => c.GetByIdAsync(Guid.NewGuid()));
        (await byId.Should().ThrowAsync<HttpRequestException>("offline is not 'not found'")).WithMessage("Sin conexión*");

        var kitchen = () => OnSatelliteKitchen(k => k.GetActiveAsync());
        (await kitchen.Should().ThrowAsync<HttpRequestException>()).WithMessage("Sin conexión*");

        var kitchenById = () => OnSatelliteKitchen(k => k.GetByIdAsync(Guid.NewGuid()));
        (await kitchenById.Should().ThrowAsync<HttpRequestException>()).WithMessage("Sin conexión*");

        // The satellite's own selling side is untouched.
        var open = () => OnSatelliteComandas(c => c.OpenComandaAsync("Mesa 9", "Luis"));
        (await open.Should().ThrowAsync<HttpRequestException>()).WithMessage("Sin conexión*");
        (await _sat.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Satélite", 0m, null))).Status.Should().Be(CashShiftStatus.Open);
    }

    [Fact]
    public async Task MasterDown_RemoteCloseIsNotPossible_ByDesign_NoOrderNoOutbox()
    {
        var comanda = await OpenMesa5WithItemsSentAsync();
        var shift = await OpenSatelliteShiftAsync();
        await _fx.Server.StopAsync();

        var act = () => OnSatelliteComandas(c => c.CloseAndPayAsync(comanda.Id, shift.Id, PaymentMethod.Cash, comanda.Total));

        (await act.Should().ThrowAsync<HttpRequestException>("the lock requires the master; without it no invoice is cut")).WithMessage("Sin conexión*");
        (await _sat.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(0);
        (await _sat.WithDbAsync(db => db.OrderItems.CountAsync())).Should().Be(0);
        (await _sat.WithDbAsync(db => db.Payments.CountAsync())).Should().Be(0);
        (await _sat.WithDbAsync(db => db.OutboxMessages.Select(m => m.EventType).ToListAsync())).Should().Equal(SyncEventTypes.CashShiftOpened);

        // Back online (new ephemeral port → repoint), the comanda is exactly as it was: Open, unlocked, and now payable.
        await _fx.Server.StartAsync();
        _sat.TerminalSettings.MasterUrl = _fx.BaseUrl;
        _sat.SyncSettings.WebhookUrl = _fx.BaseUrl + "/api/sync/inbox";
        var master = await OnSatelliteComandas(c => c.GetByIdAsync(comanda.Id));
        master!.Status.Should().Be(ComandaStatus.Open);
        master.PaymentLockedByTerminalCode.Should().BeNull();
        var pay = await OnSatelliteComandas(c => c.CloseAndPayAsync(comanda.Id, shift.Id, PaymentMethod.Cash, comanda.Total));
        pay.Success.Should().BeTrue(pay.ErrorMessage);
        (await _sat.WithDbAsync(db => db.Orders.CountAsync())).Should().Be(1);
    }
}
