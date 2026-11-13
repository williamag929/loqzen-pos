using System.Globalization;
using Core.Application.DTOs;
using Core.Application.Sync;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// End-to-end "segunda caja": a satellite container (own SQLite, TerminalContext CAJA-02) talks to the master's
/// real Kestrel over loopback. Catalog pull, heartbeat, a full shift (sale, cash movement, recaudo, close) replicated
/// through the real HttpOutboxSink + OutboxDispatcher into the master's /api/sync/inbox, then the consolidated day.
/// </summary>
public class MasterSatelliteRoundTripTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();
    private SatelliteContainer _sat = null!;

    public async Task InitializeAsync()
    {
        await _fx.InitializeAsync();
        _sat = await _fx.CreateSatelliteAsync();
    }

    public async Task DisposeAsync()
    {
        await _sat.DisposeAsync();
        await _fx.DisposeAsync();
    }

    private DateTime Now => _fx.Clock.GetUtcNow().UtcDateTime;
    private DateOnly ClockLocalDay => DateOnly.FromDateTime(_fx.Clock.GetLocalNow().DateTime);

    // ── catalog ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CatalogPull_CopiesTheMasterCatalog_PreservingIds()
    {
        var masterProducts = await _fx.WithDbAsync(db => db.Products.AsNoTracking().OrderBy(p => p.Sku).ToListAsync());
        var masterCounts = await _fx.WithDbAsync(async db => await db.Categories.CountAsync() + await db.Taxes.CountAsync()
            + await db.Products.CountAsync() + await db.Billers.CountAsync() + await db.Users.CountAsync());

        var result = await _sat.PullCatalogAsync();

        result.Inserted.Should().Be(masterCounts);
        result.Updated.Should().Be(0);
        result.Deactivated.Should().Be(0);

        var satProducts = await _sat.WithDbAsync(db => db.Products.AsNoTracking().Include(p => p.Tax).Include(p => p.Category).OrderBy(p => p.Sku).ToListAsync());
        satProducts.Select(p => p.Id).Should().Equal(masterProducts.Select(p => p.Id));
        satProducts.Select(p => (p.Sku, p.Name, p.Price, p.CategoryId, p.TaxId, p.IsActive))
            .Should().Equal(masterProducts.Select(p => (p.Sku, p.Name, p.Price, p.CategoryId, p.TaxId, p.IsActive)));
        satProducts.Should().OnlyContain(p => p.Tax != null && p.Category != null, "FK targets came in the same snapshot");

        // Users (same PIN hashes) and billers too.
        var masterUsers = await _fx.WithDbAsync(db => db.Users.AsNoTracking().OrderBy(u => u.Name).Select(u => new { u.Id, u.Name, u.PinHash, u.Role }).ToListAsync());
        var satUsers = await _sat.WithDbAsync(db => db.Users.AsNoTracking().OrderBy(u => u.Name).Select(u => new { u.Id, u.Name, u.PinHash, u.Role }).ToListAsync());
        satUsers.Should().BeEquivalentTo(masterUsers);
        (await _sat.WithDbAsync(db => db.Billers.CountAsync())).Should().Be(await _fx.WithDbAsync(db => db.Billers.CountAsync()));

        // Own row remembers the pull.
        var self = await _sat.WithDbAsync(db => db.Terminals.SingleAsync());
        self.Code.Should().Be("CAJA-02");
        self.Kind.Should().Be(TerminalKind.Satellite);
        self.LastCatalogPullAt.Should().Be(Now);

        // Second pull: idempotent upsert.
        var again = await _sat.PullCatalogAsync();
        again.Inserted.Should().Be(0);
        again.Updated.Should().Be(masterCounts);
        (await _sat.WithDbAsync(db => db.Products.CountAsync())).Should().Be(masterProducts.Count);
    }

    [Fact]
    public async Task CatalogPull_PropagatesPriceChangesAndDeactivations()
    {
        await _sat.PullCatalogAsync();
        await _fx.WithDbAsync(async db =>
        {
            var burger = await db.Products.SingleAsync(p => p.Sku == "BUR-001");
            burger.Price = 25000m;
            var soda = await db.Products.SingleAsync(p => p.Sku == "BEB-001");
            soda.IsActive = false;
            await db.SaveChangesAsync();
        });

        await _sat.PullCatalogAsync();

        (await _sat.WithDbAsync(db => db.Products.SingleAsync(p => p.Sku == "BUR-001"))).Price.Should().Be(25000m);
        (await _sat.WithDbAsync(db => db.Products.SingleAsync(p => p.Sku == "BEB-001"))).IsActive.Should().BeFalse();
    }

    /// <summary>
    /// The desktop runs DbInitializer on EVERY install, so a fresh satellite already holds the demo products/billers/users
    /// under its own ids before the first pull. The snapshot carries the master's rows under different ids but the same
    /// Sku / Name (unique indexes). Unreferenced local twins are deleted so the master's rows can come in.
    /// </summary>
    [Fact]
    public async Task CatalogPull_OnASatelliteSeededByDbInitializer_ReplacesUnreferencedTwinsWithTheMastersRows()
    {
        await using var seeded = await _fx.CreateSatelliteAsync("CAJA-03", "Caja 3", seedDemoData: true);
        var localBurger = await seeded.WithDbAsync(db => db.Products.Where(p => p.Sku == "BUR-001").Select(p => p.Id).SingleAsync());
        var masterBurger = await _fx.WithDbAsync(db => db.Products.Where(p => p.Sku == "BUR-001").Select(p => p.Id).SingleAsync());
        localBurger.Should().NotBe(masterBurger, "each install seeds its own ids");

        var result = await seeded.PullCatalogAsync();

        result.Deactivated.Should().Be(0, "nothing local was referenced by a sale");
        var masterProducts = await _fx.WithDbAsync(db => db.Products.Select(p => new { p.Id, p.Sku, p.IsActive }).ToListAsync());
        var satProducts = await seeded.WithDbAsync(db => db.Products.Select(p => new { p.Id, p.Sku, p.IsActive }).ToListAsync());
        satProducts.Should().BeEquivalentTo(masterProducts, "the seeded twins were deleted and the master's rows inserted");
        (await seeded.WithDbAsync(db => db.Products.AnyAsync(p => p.Id == localBurger))).Should().BeFalse();

        var masterBillers = await _fx.WithDbAsync(db => db.Billers.Select(b => new { b.Id, b.Name }).ToListAsync());
        (await seeded.WithDbAsync(db => db.Billers.Select(b => new { b.Id, b.Name }).ToListAsync())).Should().BeEquivalentTo(masterBillers);
        var masterUsers = await _fx.WithDbAsync(db => db.Users.Select(u => new { u.Id, u.Name, u.PinHash }).ToListAsync());
        (await seeded.WithDbAsync(db => db.Users.Select(u => new { u.Id, u.Name, u.PinHash }).ToListAsync())).Should().BeEquivalentTo(masterUsers);
        var masterCategories = await _fx.WithDbAsync(db => db.Categories.Select(c => new { c.Id, c.Name }).ToListAsync());
        (await seeded.WithDbAsync(db => db.Categories.Select(c => new { c.Id, c.Name }).ToListAsync())).Should().BeEquivalentTo(masterCategories);

        // Own terminal row untouched.
        (await seeded.WithDbAsync(db => db.Terminals.Select(t => t.Code).ToListAsync())).Should().Equal("CAJA-03");
    }

    /// <summary>A seeded twin that a local sale already references cannot be deleted: it is mangled + deactivated, the sale keeps pointing at it.</summary>
    [Fact]
    public async Task CatalogPull_OnASeededSatellite_KeepsAReferencedTwin_MangledAndInactive()
    {
        await using var seeded = await _fx.CreateSatelliteAsync("CAJA-03", "Caja 3", seedDemoData: true);
        var localSoda = await seeded.WithDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Sku == "BEB-001"));
        var shift = await seeded.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero 3", 0m, null));
        var orderId = await seeded.WithServiceAsync<ISalesService, Guid>(async sales =>
        {
            var o = await sales.CreateOrderAsync(null, shift.Id);
            o = await sales.AddItemAsync(o.Id, localSoda.Id, 1m);
            (await sales.ProcessPaymentAsync(o.Id, PaymentMethod.Cash, o.Total, o.Total)).Success.Should().BeTrue();
            return o.Id;
        });

        var result = await seeded.PullCatalogAsync();

        result.Deactivated.Should().BeGreaterThanOrEqualTo(1, "the referenced twin is kept but deactivated");
        var masterSodaId = await _fx.WithDbAsync(db => db.Products.Where(p => p.Sku == "BEB-001").Select(p => p.Id).SingleAsync());
        var sodas = await seeded.WithDbAsync(db => db.Products.AsNoTracking().Where(p => p.Id == localSoda.Id || p.Id == masterSodaId).ToListAsync());
        sodas.Should().HaveCount(2);
        var kept = sodas.Single(p => p.Id == localSoda.Id);
        kept.IsActive.Should().BeFalse();
        kept.Sku.Should().NotBe("BEB-001", "the natural key must be freed for the master's row");
        var incoming = sodas.Single(p => p.Id == masterSodaId);
        incoming.IsActive.Should().BeTrue();
        incoming.Sku.Should().Be("BEB-001");
        var item = await seeded.WithDbAsync(db => db.OrderItems.AsNoTracking().SingleAsync(i => i.OrderId == orderId));
        item.ProductId.Should().Be(localSoda.Id, "history keeps its original reference");
        (await seeded.WithDbAsync(db => db.Products.CountAsync(p => p.IsActive))).Should().Be(await _fx.WithDbAsync(db => db.Products.CountAsync(p => p.IsActive)));
    }

    // ── heartbeat ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_MakesTheSatelliteVisibleOnTheMaster()
    {
        var mine = await _sat.HeartbeatAsync("2.0.0");

        mine.Code.Should().Be("CAJA-02");
        mine.Kind.Should().Be(TerminalKind.Satellite);
        mine.LastSeenAt.Should().Be(Now);
        mine.IsOnline(Now, TimeSpan.FromSeconds(90)).Should().BeTrue();

        var list = (await _fx.GetJsonAsync("/api/terminals")).AsArray();
        list.Select(t => t!["code"]!.GetValue<string>()).Should().Equal("CAJA-01", "CAJA-02");
        var row = list[1]!;
        row["kind"]!.GetValue<string>().Should().Be("Satellite");
        row["appVersion"]!.GetValue<string>().Should().Be("2.0.0");
        row["lastSeenAt"].Should().NotBeNull();
        row["openShiftNumber"].Should().BeNull();

        (await _sat.MasterApi.PingAsync()).Should().BeTrue();
    }

    // ── full round trip ──────────────────────────────────────────────────────

    [Fact]
    public async Task FullShiftOnTheSatellite_IsReplicatedToTheMaster_AndConsolidated()
    {
        await _sat.PullCatalogAsync();
        await _sat.HeartbeatAsync();

        // 1. Open a shift on the satellite.
        var satShift = await _sat.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Satélite", 100000m, null));
        satShift.TerminalCode.Should().Be("CAJA-02");
        satShift.TerminalId.Should().Be(_sat.Terminal.Id);
        satShift.ShiftNumber.Should().Be(1);

        // 2. Sell 2 x Gaseosa (master's product id) for cash.
        var soda = await _sat.WithDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Sku == "BEB-001"));
        var masterSodaId = await _fx.WithDbAsync(db => db.Products.Where(p => p.Sku == "BEB-001").Select(p => p.Id).SingleAsync());
        soda.Id.Should().Be(masterSodaId);
        var order = await _sat.WithServiceAsync<ISalesService, OrderDto>(async sales =>
        {
            var o = await sales.CreateOrderAsync(null, satShift.Id);
            o = await sales.AddItemAsync(o.Id, soda.Id, 2m);
            var pay = await sales.ProcessPaymentAsync(o.Id, PaymentMethod.Cash, o.Total, o.Total);
            pay.Success.Should().BeTrue(pay.ErrorMessage);
            return (await sales.GetByIdAsync(o.Id))!;
        });
        order.Status.Should().Be(OrderStatus.Completed);
        order.Total.Should().BeGreaterThan(0);

        // 3. A manual cash-in (no manager needed) and a cash recaudo (Energía: 1500 fixed commission).
        var movement = await _sat.WithServiceAsync<ICashShiftService, CashMovementDto>(s =>
            s.RecordMovementAsync(CashOpsData.Movement(CashMovementType.PaidIn, 20000m, "Sencillo")));
        var energy = await _sat.WithDbAsync(db => db.Billers.AsNoTracking().SingleAsync(b => b.Code == "ENERGIA"));
        var bill = await _sat.WithServiceAsync<IBillPaymentService, BillPaymentDto>(b =>
            b.RecordAsync(new BillPaymentRequest(energy.Id, "CTA-778899", 50000m, PaymentMethod.Cash, 60000m, null, "Cajero Satélite", "Doña Rosa")));
        bill.Commission.Should().Be(1500m);
        bill.Total.Should().Be(51500m);

        // 4. A kitchen ticket event (not replicated by the master: must be acknowledged as Ignored and marked Synced).
        await _sat.WithDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = "KitchenOrderCreated", AggregateType = "KitchenOrder", AggregateId = Guid.NewGuid(),
                PayloadJson = "{\"kitchenOrderId\":\"" + Guid.NewGuid() + "\",\"table\":\"Mesa 1\",\"items\":1}",
                Status = SyncStatus.Pending, CreatedAt = Now, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        });

        var pendingBeforeFirstPass = await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Status == SyncStatus.Pending));
        pendingBeforeFirstPass.Should().Be(5, "CashShiftOpened, OrderCompleted, CashMovementRecorded, BillPaymentRecorded, KitchenOrderCreated");

        // 5. First dispatcher pass: everything so far reaches the master.
        var first = await _sat.DispatchAsync();
        first.Should().Be(new OutboxDispatchResult(5, 0, 0));
        (await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Status != SyncStatus.Synced))).Should().Be(0);

        var receipts = await _fx.WithServiceAsync<ISyncInboxService, IReadOnlyList<SyncInboxReceiptDto>>(i => i.GetRecentAsync());
        receipts.Should().HaveCount(5);
        receipts.Should().OnlyContain(r => r.TerminalCode == "CAJA-02");
        receipts.Single(r => r.EventType == "KitchenOrderCreated").Outcome.Should().Be(SyncInboxOutcome.Ignored);
        receipts.Where(r => r.EventType != "KitchenOrderCreated").Should().OnlyContain(r => r.Outcome == SyncInboxOutcome.Applied);

        // The replicated shift is OPEN on the master but is NOT the master's current shift.
        var replicatedOpen = await _fx.WithDbAsync(db => db.CashShifts.AsNoTracking().SingleAsync(s => s.Id == satShift.Id));
        replicatedOpen.Status.Should().Be(CashShiftStatus.Open);
        replicatedOpen.TerminalCode.Should().Be("CAJA-02");
        replicatedOpen.CashierName.Should().Be("Cajero Satélite");
        (await _fx.WithServiceAsync<ICashShiftService, CashShift?>(s => s.GetCurrentShiftAsync()))
            .Should().BeNull("the master has not opened its own shift; the satellite's open shift must not leak into it");
        var masterTerminals = (await _fx.GetJsonAsync("/api/terminals")).AsArray();
        masterTerminals[1]!["openShiftNumber"]!.GetValue<int>().Should().Be(1, "the master sees CAJA-02's open shift");
        masterTerminals[1]!["openShiftCashier"]!.GetValue<string>().Should().Be("Cajero Satélite");

        // 6. Close the satellite shift with a perfect count (blind), then replicate.
        var reconciliation = await _sat.WithServiceAsync<ICashShiftService, ShiftReconciliationDto>(s => s.GetReconciliationAsync());
        reconciliation.ExpectedCash.Should().Be(100000m + order.Total + 20000m + 51500m);
        _fx.Clock.Advance(TimeSpan.FromHours(2));
        var closed = await _sat.WithServiceAsync<ICashShiftService, ShiftReconciliationDto>(s =>
            s.CloseShiftAsync(CashOpsData.Close(reconciliation.ExpectedCash, floatLeft: 50000m, closedBy: "Cajero Satélite")));
        closed.Status.Should().Be(CashShiftStatus.Closed);
        closed.Difference.Should().Be(0m);

        var second = await _sat.DispatchAsync();
        second.Should().Be(new OutboxDispatchResult(1, 0, 0));

        // 7. Master database: shift (closed, same id/number/counted), order, movement, bill payment, all CAJA-02.
        var masterSender = await _fx.WithDbAsync(db => db.Terminals.AsNoTracking().SingleAsync(t => t.Code == "CAJA-02"));
        masterSender.LastSyncAt.Should().Be(Now);

        var masterShift = await _fx.WithDbAsync(db => db.CashShifts.AsNoTracking().SingleAsync(s => s.Id == satShift.Id));
        masterShift.Status.Should().Be(CashShiftStatus.Closed);
        masterShift.ShiftNumber.Should().Be(1);
        masterShift.TerminalCode.Should().Be("CAJA-02");
        masterShift.TerminalId.Should().Be(masterSender.Id, "replicated rows reference the master's Terminal row");
        masterShift.OpeningAmount.Should().Be(100000m);
        masterShift.ClosingAmount.Should().Be(reconciliation.ExpectedCash);
        masterShift.ExpectedAmount.Should().Be(reconciliation.ExpectedCash);
        masterShift.FloatLeftAmount.Should().Be(50000m);
        masterShift.WasBlindCount.Should().BeTrue();
        masterShift.ClosedByName.Should().Be("Cajero Satélite");
        masterShift.ClosedAt.Should().Be(Now);
        (await _fx.WithDbAsync(db => db.CashCountLines.Where(l => l.CashShiftId == satShift.Id).ToListAsync()))
            .Should().ContainSingle().Which.Denomination.Should().Be(reconciliation.ExpectedCash);

        var masterOrder = await _fx.WithDbAsync(db => db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Payments).SingleAsync(o => o.Id == order.Id));
        masterOrder.TerminalCode.Should().Be("CAJA-02");
        masterOrder.TerminalId.Should().Be(masterSender.Id);
        masterOrder.CashShiftId.Should().Be(satShift.Id);
        masterOrder.Status.Should().Be(OrderStatus.Completed);
        masterOrder.Total.Should().Be(order.Total);
        masterOrder.OrderNumber.Should().Be(order.OrderNumber);
        masterOrder.Items.Should().ContainSingle().Which.ProductId.Should().Be(masterSodaId, "ids are the master's catalog ids");
        masterOrder.Items.Single().Quantity.Should().Be(2m);
        masterOrder.Payments.Should().ContainSingle().Which.Amount.Should().Be(order.Total);
        masterOrder.Payments.Single().Method.Should().Be(PaymentMethod.Cash);

        var masterMovement = await _fx.WithDbAsync(db => db.CashMovements.AsNoTracking().SingleAsync(m => m.Id == movement.Id));
        masterMovement.CashShiftId.Should().Be(satShift.Id);
        masterMovement.Type.Should().Be(CashMovementType.PaidIn);
        masterMovement.Amount.Should().Be(20000m);
        masterMovement.Reason.Should().Be("Sencillo");

        var masterBill = await _fx.WithDbAsync(db => db.BillPayments.AsNoTracking().SingleAsync(b => b.Id == bill.Id));
        masterBill.TerminalCode.Should().Be("CAJA-02");
        masterBill.TerminalId.Should().Be(masterSender.Id);
        masterBill.CashShiftId.Should().Be(satShift.Id);
        masterBill.BillerId.Should().Be(energy.Id);
        masterBill.Amount.Should().Be(50000m);
        masterBill.Commission.Should().Be(1500m);
        masterBill.ReceiptNumber.Should().Be(bill.ReceiptNumber);
        masterBill.Status.Should().Be(BillPaymentStatus.Completed);
        masterBill.CustomerName.Should().Be("Doña Rosa");

        // The master's own reconciliation of the replicated shift agrees with the satellite's.
        var masterView = await _fx.WithServiceAsync<ICashShiftService, ShiftReconciliationDto>(s => s.GetReconciliationAsync(satShift.Id));
        masterView.CashSales.Should().Be(order.Total);
        masterView.PaidIn.Should().Be(20000m);
        masterView.BillPaymentsCash.Should().Be(51500m);
        masterView.ExpectedCash.Should().Be(reconciliation.ExpectedCash);
        masterView.CountedCash.Should().Be(reconciliation.ExpectedCash);
        masterView.TerminalCode.Should().Be("CAJA-02");

        // 8. The master opens its own shift: its current shift is its own, never the satellite's.
        var masterShiftOwn = await _fx.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Principal", 30000m, null));
        masterShiftOwn.TerminalCode.Should().Be("CAJA-01");
        masterShiftOwn.ShiftNumber.Should().Be(1, "ShiftNumber is a per-register sequence");
        var current = await _fx.WithServiceAsync<ICashShiftService, CashShift?>(s => s.GetCurrentShiftAsync());
        current!.Id.Should().Be(masterShiftOwn.Id).And.NotBe(satShift.Id);
        (await _fx.WithServiceAsync<ICashShiftService, IReadOnlyList<ShiftHistoryItemDto>>(s => s.GetHistoryAsync()))
            .Should().NotContain(h => h.ShiftId == satShift.Id, "history is scoped to this register");

        // 9. Consolidated day: CAJA-01 first, then CAJA-02 whose cash sales equal the satellite's sale.
        var day = await _fx.GetJsonAsync($"/api/reports/day?date={ClockLocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        var terminals = day["terminals"]!.AsArray();
        terminals.Select(t => t!["terminalCode"]!.GetValue<string>()).Should().Equal("CAJA-01", "CAJA-02");
        var caja2 = terminals[1]!;
        caja2["terminalName"]!.GetValue<string>().Should().Be("Caja 2");
        caja2["terminalId"]!.GetValue<Guid>().Should().Be(masterSender.Id);
        caja2["shiftCount"]!.GetValue<int>().Should().Be(1);
        caja2["openShiftCount"]!.GetValue<int>().Should().Be(0);
        caja2["orderCount"]!.GetValue<int>().Should().Be(1);
        caja2["cashSales"]!.GetValue<decimal>().Should().Be(order.Total);
        caja2["totalSales"]!.GetValue<decimal>().Should().Be(order.Total);
        caja2["billPaymentCount"]!.GetValue<int>().Should().Be(1);
        caja2["billPaymentsTotal"]!.GetValue<decimal>().Should().Be(50000m);
        caja2["billPaymentCommission"]!.GetValue<decimal>().Should().Be(1500m);
        caja2["cashIn"]!.GetValue<decimal>().Should().Be(20000m);
        caja2["cashOut"]!.GetValue<decimal>().Should().Be(0m);
        caja2["difference"]!.GetValue<decimal>().Should().Be(0m);
        caja2["shifts"]!.AsArray().Should().ContainSingle().Which["shiftId"]!.GetValue<Guid>().Should().Be(satShift.Id);
        var caja1 = terminals[0]!;
        caja1["openShiftCount"]!.GetValue<int>().Should().Be(1);
        caja1["cashSales"]!.GetValue<decimal>().Should().Be(0m);
        day["cashSales"]!.GetValue<decimal>().Should().Be(order.Total);
        day["openShiftCount"]!.GetValue<int>().Should().Be(1);
        day["billPaymentsTotal"]!.GetValue<decimal>().Should().Be(50000m);

        // 10. Nothing left to send.
        var third = await _sat.DispatchAsync();
        third.Should().Be(new OutboxDispatchResult(0, 0, 0));
        (await _sat.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Status == SyncStatus.Synced))).Should().Be(6);
        (await _fx.WithDbAsync(db => db.SyncInboxReceipts.CountAsync())).Should().Be(6);
    }

    [Fact]
    public async Task RetriedMessage_IsADuplicateOnTheMaster_AndNothingIsDoubled()
    {
        await _sat.PullCatalogAsync();
        var shift = await _sat.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Satélite", 0m, null));
        var soda = await _sat.WithDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Sku == "BEB-001"));
        var orderId = await _sat.WithServiceAsync<ISalesService, Guid>(async sales =>
        {
            var o = await sales.CreateOrderAsync(null, shift.Id);
            o = await sales.AddItemAsync(o.Id, soda.Id, 1m);
            (await sales.ProcessPaymentAsync(o.Id, PaymentMethod.Cash, o.Total, o.Total)).Success.Should().BeTrue();
            return o.Id;
        });
        (await _sat.DispatchAsync()).Sent.Should().Be(2);

        // Simulate "the master committed but the satellite never saw the 200": put the messages back to Pending.
        await _sat.WithDbAsync(async db =>
        {
            foreach (var m in await db.OutboxMessages.ToListAsync()) { m.Status = SyncStatus.Pending; m.ProcessedAt = null; }
            await db.SaveChangesAsync();
        });

        var retry = await _sat.DispatchAsync();

        retry.Should().Be(new OutboxDispatchResult(2, 0, 0), "Duplicate is an acknowledgement");
        (await _fx.WithDbAsync(db => db.Orders.CountAsync(o => o.Id == orderId))).Should().Be(1);
        (await _fx.WithDbAsync(db => db.CashShifts.CountAsync(s => s.Id == shift.Id))).Should().Be(1);
        (await _fx.WithDbAsync(db => db.SyncInboxReceipts.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task SatelliteCannotApplyInboxEnvelopes_AndMasterCannotApplyASnapshot()
    {
        var onSatellite = await _sat.WithServiceAsync<ISyncInboxService, SyncInboxResult>(i => i.ApplyAsync(new SyncEnvelopeDto
        {
            Id = Guid.NewGuid(), EventType = "KitchenOrderCreated", AggregateType = "KitchenOrder", AggregateId = Guid.NewGuid(),
            OccurredAt = Now, TerminalCode = "CAJA-01"
        }));
        onSatellite.Outcome.Should().Be(SyncInboxOutcome.Rejected);

        var snapshot = await _sat.MasterApi.GetSnapshotAsync();
        var act = () => _fx.WithServiceAsync<ICatalogSyncService, CatalogApplyResult>(c => c.ApplySnapshotAsync(snapshot));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── master offline ───────────────────────────────────────────────────────

    [Fact]
    public async Task MasterDown_DispatcherRecordsAFailedAttempt_AndKeepsTheMessagePending()
    {
        await _sat.PullCatalogAsync();
        var shift = await _sat.WithServiceAsync<ICashShiftService, CashShift>(s => s.OpenShiftAsync("Cajero Satélite", 5000m, null));
        var opened = await _sat.WithDbAsync(db => db.OutboxMessages.SingleAsync(m => m.EventType == SyncEventTypes.CashShiftOpened));

        await _fx.Server.StopAsync();
        _fx.Server.IsRunning.Should().BeFalse();
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await _sat.DispatchAsync();

        result.Should().Be(new OutboxDispatchResult(0, 1, 0));
        var after = await _sat.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == opened.Id));
        after.Status.Should().Be(SyncStatus.Pending, "one failure is far below MaxRetries");
        after.RetryCount.Should().Be(1);
        after.LastError.Should().NotBeNullOrWhiteSpace();
        after.ProcessedAt.Should().BeNull();
        after.UpdatedAt.Should().Be(Now, "the attempt time drives the backoff");
        after.AggregateId.Should().Be(shift.Id);

        // The selling side is untouched: the shift stays open locally.
        (await _sat.WithServiceAsync<ICashShiftService, CashShift?>(s => s.GetCurrentShiftAsync()))!.Id.Should().Be(shift.Id);

        // Immediately again: inside the 2 s backoff, skipped, no second failure recorded.
        var again = await _sat.DispatchAsync();
        again.Should().Be(new OutboxDispatchResult(0, 0, 1));
        (await _sat.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == opened.Id))).RetryCount.Should().Be(1);

        // MasterApiClient reports the master as unreachable too (transport error, not a hang).
        var ping = () => _sat.MasterApi.PingAsync();
        await ping.Should().ThrowAsync<HttpRequestException>();
    }
}
