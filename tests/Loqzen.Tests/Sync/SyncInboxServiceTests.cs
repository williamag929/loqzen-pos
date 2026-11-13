using System.Net;
using System.Text.Json;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Application.Sync;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Outbox;
using Infrastructure.Data.Services;
using Infrastructure.Data.Sync;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static Loqzen.Tests.Fixtures.CashOpsData;

namespace Loqzen.Tests.Sync;

/// <summary>
/// Master inbox fed with envelopes produced by a REAL satellite: a second SQLite database where
/// CashShiftService / SalesService / BillPaymentService run under a Satellite context, whose OutboxMessages are
/// pushed through HttpOutboxSink (captured) and deserialized exactly as /api/sync/inbox would receive them.
/// </summary>
public class SyncInboxServiceTests : IDisposable
{
    private readonly SqliteDb _masterDb = SqliteDb.Create();
    private readonly SqliteDb _satDb = SqliteDb.Create();
    private readonly TestClock _clock = new();
    private readonly CashShiftSettings _settings = new() { ManagerRequiredFor = new List<string>() };
    private readonly TerminalContext _master = Terminals.Master("CAJA-01");
    private readonly TerminalContext _sat = Terminals.Satellite("CAJA-02");

    private readonly PosDbContext _m;   // master
    private readonly PosDbContext _s;   // satellite
    private readonly SyncInboxService _inbox;
    private readonly HashSet<Guid> _seen = new();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public SyncInboxServiceTests()
    {
        _m = _masterDb.NewContext();
        _s = _satDb.NewContext();
        _inbox = new SyncInboxService(_m, _clock, _master);
        new TerminalService(_m, _clock, _master).EnsureRegisteredAsync(_master.Code, _master.Name, TerminalKind.Master).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _m.Dispose(); _s.Dispose();
        _masterDb.Dispose(); _satDb.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── satellite side ───────────────────────────────────────────────────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Envelopes of the satellite's Outbox messages not yet collected, in creation order, built by the real sink.</summary>
    private async Task<List<SyncEnvelopeDto>> NewEnvelopesAsync()
    {
        var handler = new CaptureHandler();
        var sink = new HttpOutboxSink(new HttpClient(handler), new SyncSettings { Enabled = true, WebhookUrl = "http://master/api/sync/inbox" }, _sat);
        var list = new List<SyncEnvelopeDto>();
        foreach (var msg in await _s.OutboxMessages.AsNoTracking().ToListAsync())
        {
            if (!_seen.Add(msg.Id)) continue;
            await sink.DeliverAsync(msg);
            list.Add(JsonSerializer.Deserialize<SyncEnvelopeDto>(handler.Body!, Web)!);
        }
        return list;
    }

    private async Task<SyncEnvelopeDto> NewEnvelopeAsync(string eventType)
    {
        var all = await NewEnvelopesAsync();
        return all.Single(e => e.EventType == eventType);
    }

    private static SyncEnvelopeDto Clone(SyncEnvelopeDto e, Action<SyncEnvelopeDto>? mutate = null)
    {
        var c = new SyncEnvelopeDto { Id = e.Id, EventType = e.EventType, AggregateType = e.AggregateType, AggregateId = e.AggregateId, OccurredAt = e.OccurredAt, TerminalCode = e.TerminalCode, Payload = e.Payload };
        mutate?.Invoke(c);
        return c;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private CashShiftService SatShifts() => new(_s, _clock, _settings, _sat);
    private BillPaymentService SatBills() => new(_s, _clock, _sat);

    /// <summary>Products/billers created on the master and pulled by the satellite (ids preserved), as in production.</summary>
    private async Task<(Product product, Biller biller)> SyncCatalogAsync()
    {
        var product = new Product { Sku = "PAN-1", Name = "Pan", Price = 10000m };
        var biller = new Biller { Name = "Energia", CommissionFixed = 1500m, SortOrder = 1 };
        _m.AddRange(product, biller);
        await _m.SaveChangesAsync();
        var snap = await new CatalogSyncService(_m, _clock, _master).BuildSnapshotAsync();
        await new CatalogSyncService(_s, _clock, _sat).ApplySnapshotAsync(snap);
        return (product, biller);
    }

    private sealed record SatDay(Guid ShiftId, Guid OrderId, Guid BillId, Guid VoidedBillId, Guid MovementId);

    /// <summary>
    /// Satellite: open (Ana, 100.000) → sale 3 × 10.000 cash → recaudo 50.000 (+1.500) cash → recaudo 40.000 cash voided
    /// → PaidOut 5.000 → close counting 176.500 exactly (6 lines), float 100.000. Seven Outbox messages.
    /// </summary>
    private async Task<SatDay> RunSatelliteDayAsync()
    {
        var (product, biller) = await SyncCatalogAsync();
        var shifts = SatShifts();
        var shift = await shifts.OpenShiftAsync("Ana", 100000m, Guid.Parse("00000000-0000-0000-0000-0000000000a1"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        var sales = new SalesService(_s, _sat);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 3);
        (await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Cash, order.Total, 50000m)).Success.Should().BeTrue();

        _clock.Advance(TimeSpan.FromMinutes(1));
        var bills = SatBills();
        var bill = await bills.RecordAsync(new BillPaymentRequest(biller.Id, "CTA-1", 50000m, PaymentMethod.Cash, 60000m, null, "Ana", "Cliente"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var voided = await bills.RecordAsync(new BillPaymentRequest(biller.Id, "CTA-2", 40000m, PaymentMethod.Cash, 41500m, null, "Ana"));
        var oid = await AddOverrideAsync(_s, _clock, AuthorizedAction.VoidBillPayment);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await bills.VoidAsync(voided.Id, "Se arrepintió", oid);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var mv = await shifts.RecordMovementAsync(Movement(CashMovementType.PaidOut, 5000m, "Hielo"));

        _clock.Advance(TimeSpan.FromHours(8));
        var lines = new[]
        {
            new CashCountLineDto { Denomination = 100000m, Quantity = 1 }, new CashCountLineDto { Denomination = 50000m, Quantity = 1 },
            new CashCountLineDto { Denomination = 20000m, Quantity = 1 }, new CashCountLineDto { Denomination = 5000m, Quantity = 1 },
            new CashCountLineDto { Denomination = 1000m, Quantity = 1 }, new CashCountLineDto { Denomination = 500m, Quantity = 1 },
        };
        var r = await shifts.CloseShiftAsync(new CloseShiftRequest(lines, 100000m, "Cierre normal", null, "Ana", true));
        r.ExpectedCash.Should().Be(176500m);
        r.Difference.Should().Be(0m);
        _s.ChangeTracker.Clear();
        return new SatDay(shift.Id, order.Id, bill.Id, voided.Id, mv.Id);
    }

    // ── full replication ─────────────────────────────────────────────────────

    [Fact]
    public async Task FullSatelliteDay_EveryEnvelopeApplied_MasterReconciliationEqualsSatellite()
    {
        var day = await RunSatelliteDayAsync();
        var envelopes = await NewEnvelopesAsync();
        envelopes.Select(e => e.EventType).Should().BeEquivalentTo(new[]
        {
            SyncEventTypes.CashShiftOpened, SyncEventTypes.OrderCompleted, SyncEventTypes.BillPaymentRecorded,
            SyncEventTypes.BillPaymentRecorded, SyncEventTypes.BillPaymentVoided, SyncEventTypes.CashMovementRecorded, SyncEventTypes.CashShiftClosed
        });
        envelopes.Should().OnlyContain(e => e.TerminalCode == "CAJA-02");

        // Apply in a sensible order (opened first, closed last); the rest as produced.
        var ordered = envelopes.OrderBy(e => e.EventType == SyncEventTypes.CashShiftOpened ? 0 : e.EventType == SyncEventTypes.CashShiftClosed ? 2 : 1).ToList();
        foreach (var e in ordered)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            var result = await _inbox.ApplyAsync(e);
            result.Outcome.Should().Be(SyncInboxOutcome.Applied, $"{e.EventType}: {result.Error}");
            result.IsAcknowledged.Should().BeTrue();
        }

        // Receipts: one per message, all Applied, newest first.
        var receipts = await _inbox.GetRecentAsync();
        receipts.Should().HaveCount(7);
        receipts.Should().OnlyContain(r => r.Outcome == SyncInboxOutcome.Applied && r.TerminalCode == "CAJA-02" && r.Error == null);
        receipts.Select(r => r.ReceivedAt).Should().BeInDescendingOrder();
        receipts.Select(r => r.MessageId).Should().BeEquivalentTo(envelopes.Select(e => e.Id));

        // Sender auto-created with the id the satellite reported, stamped LastSyncAt.
        var sender = await _m.Terminals.AsNoTracking().SingleAsync(t => t.Code == "CAJA-02");
        sender.Id.Should().Be(_sat.Id, "the reported terminalId was free on the master");
        sender.Kind.Should().Be(TerminalKind.Satellite);
        sender.Name.Should().Be("CAJA-02");
        sender.LastSyncAt.Should().Be(Now);
        sender.LastSeenAt.Should().Be(Now);

        // Shift replicated with every field.
        var satShift = await _s.CashShifts.AsNoTracking().SingleAsync(x => x.Id == day.ShiftId);
        var shift = await _m.CashShifts.AsNoTracking().SingleAsync(x => x.Id == day.ShiftId);
        shift.TerminalId.Should().Be(sender.Id);
        shift.TerminalCode.Should().Be("CAJA-02");
        shift.ShiftNumber.Should().Be(1);
        shift.CashierName.Should().Be("Ana");
        shift.OpenedByUserId.Should().Be(satShift.OpenedByUserId);
        shift.Status.Should().Be(CashShiftStatus.Closed);
        shift.OpenedAt.Should().Be(satShift.OpenedAt);
        shift.ClosedAt.Should().Be(satShift.ClosedAt);
        shift.OpeningAmount.Should().Be(100000m);
        shift.ExpectedAmount.Should().Be(176500m);
        shift.ClosingAmount.Should().Be(176500m);
        shift.FloatLeftAmount.Should().Be(100000m);
        shift.WasBlindCount.Should().BeTrue();
        shift.Notes.Should().Be("Cierre normal");
        shift.ClosedByName.Should().Be("Ana");
        (await _m.CashCountLines.CountAsync(l => l.CashShiftId == day.ShiftId)).Should().Be(6);

        // Order with items and payments, same ids.
        var order = await _m.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Payments).SingleAsync(o => o.Id == day.OrderId);
        var satOrder = await _s.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Payments).SingleAsync(o => o.Id == day.OrderId);
        order.OrderNumber.Should().Be(satOrder.OrderNumber);
        order.Status.Should().Be(OrderStatus.Completed);
        order.Total.Should().Be(30000m);
        order.CashShiftId.Should().Be(day.ShiftId);
        order.TerminalId.Should().Be(sender.Id);
        order.TerminalCode.Should().Be("CAJA-02");
        order.Items.Should().ContainSingle().Which.Id.Should().Be(satOrder.Items.Single().Id);
        order.Items.Single().Quantity.Should().Be(3m);
        order.Payments.Should().ContainSingle().Which.Id.Should().Be(satOrder.Payments.Single().Id);
        order.Payments.Single().Method.Should().Be(PaymentMethod.Cash);
        order.Payments.Single().TenderedAmount.Should().Be(50000m);
        order.Payments.Single().ChangeAmount.Should().Be(20000m);

        // Bill payments: one completed, one voided (with reason), same receipt numbers (no collision on the master).
        var bill = await _m.BillPayments.AsNoTracking().SingleAsync(b => b.Id == day.BillId);
        bill.Status.Should().Be(BillPaymentStatus.Completed);
        bill.ReceiptNumber.Should().Be("REC-000001");
        bill.Amount.Should().Be(50000m);
        bill.Commission.Should().Be(1500m);
        bill.CustomerName.Should().Be("Cliente");
        bill.TerminalCode.Should().Be("CAJA-02");
        bill.CashShiftId.Should().Be(day.ShiftId);
        var voided = await _m.BillPayments.AsNoTracking().SingleAsync(b => b.Id == day.VoidedBillId);
        voided.Status.Should().Be(BillPaymentStatus.Voided);
        voided.VoidReason.Should().Be("Se arrepintió");
        voided.VoidedAt.Should().NotBeNull();
        voided.ReceiptNumber.Should().Be("REC-000002");

        // Manual movement replicated; the recaudo movements are not (they are display-only on the satellite).
        var mv = await _m.CashMovements.AsNoTracking().SingleAsync(x => x.Id == day.MovementId);
        mv.Type.Should().Be(CashMovementType.PaidOut);
        mv.Amount.Should().Be(5000m);
        mv.Reason.Should().Be("Hielo");
        mv.CashShiftId.Should().Be(day.ShiftId);
        (await _m.CashMovements.CountAsync(x => x.CashShiftId == day.ShiftId)).Should().Be(1);

        // The master's reconciliation of that shift reproduces the satellite's numbers.
        var master = await new CashShiftService(_m, _clock, _settings, _master).GetReconciliationAsync(day.ShiftId);
        var satellite = await SatShifts().GetReconciliationAsync(day.ShiftId);
        master.TerminalCode.Should().Be("CAJA-02");
        master.CashSales.Should().Be(satellite.CashSales).And.Be(30000m);
        master.OrderCount.Should().Be(1);
        master.BillPaymentsCash.Should().Be(satellite.BillPaymentsCash).And.Be(51500m);
        master.BillPaymentCount.Should().Be(1);
        master.PaidOut.Should().Be(5000m);
        master.ExpectedCash.Should().Be(satellite.ExpectedCash).And.Be(176500m);
        master.CountedCash.Should().Be(176500m);
        master.Difference.Should().Be(0m);
        master.CountLines.Should().HaveCount(6);
        master.Status.Should().Be(CashShiftStatus.Closed);

        // Master scoping: the replicated satellite shift never becomes "the master's open shift".
        (await new CashShiftService(_m, _clock, _settings, _master).GetCurrentShiftAsync()).Should().BeNull();
    }

    [Fact]
    public async Task OpenSatelliteShift_ShowsInMasterTerminalList_NotAsMastersCurrentShift()
    {
        await SyncCatalogAsync();
        var shift = await SatShifts().OpenShiftAsync("Ana", 1000m);
        var opened = await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened);

        (await _inbox.ApplyAsync(opened)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var terminals = await new TerminalService(_m, _clock, _master).GetAllAsync();
        terminals.Select(t => t.Code).Should().Equal("CAJA-01", "CAJA-02");
        terminals[1].OpenShiftNumber.Should().Be(1);
        terminals[1].OpenShiftCashier.Should().Be("Ana");
        terminals[0].OpenShiftNumber.Should().BeNull();

        var masterShifts = new CashShiftService(_m, _clock, _settings, _master);
        (await masterShifts.GetCurrentShiftAsync()).Should().BeNull();
        var own = await masterShifts.OpenShiftAsync("Master", 0m);
        own.ShiftNumber.Should().Be(1, "numbering is per register: CAJA-02 #1 and CAJA-01 #1 coexist");
        own.Id.Should().NotBe(shift.Id);
    }

    // ── idempotency / outcomes ───────────────────────────────────────────────

    [Fact]
    public async Task SameMessageTwice_IsDuplicate_NoDoubleRows()
    {
        await RunSatelliteDayAsync();
        var order = await NewEnvelopeAsync(SyncEventTypes.OrderCompleted);

        (await _inbox.ApplyAsync(order)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        var second = await _inbox.ApplyAsync(order);

        second.Outcome.Should().Be(SyncInboxOutcome.Duplicate);
        second.IsAcknowledged.Should().BeTrue();
        second.Error.Should().BeNull();
        (await _m.Orders.CountAsync()).Should().Be(1);
        (await _m.OrderItems.CountAsync()).Should().Be(1);
        (await _m.Payments.CountAsync()).Should().Be(1);
        (await _m.SyncInboxReceipts.CountAsync(r => r.MessageId == order.Id)).Should().Be(1);
    }

    [Fact]
    public async Task SameOrderUnderANewMessageId_IsAppliedButNotDuplicated()
    {
        await RunSatelliteDayAsync();
        var order = await NewEnvelopeAsync(SyncEventTypes.OrderCompleted);
        (await _inbox.ApplyAsync(order)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var resent = Clone(order, e => e.Id = Guid.NewGuid());
        (await _inbox.ApplyAsync(resent)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        (await _m.Orders.CountAsync()).Should().Be(1, "an order already replicated is not re-inserted");
        (await _m.SyncInboxReceipts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ClosedEventResent_DoesNotDuplicateCountLines()
    {
        await RunSatelliteDayAsync();
        var closed = await NewEnvelopeAsync(SyncEventTypes.CashShiftClosed);
        (await _inbox.ApplyAsync(closed)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        (await _inbox.ApplyAsync(Clone(closed, e => e.Id = Guid.NewGuid()))).Outcome.Should().Be(SyncInboxOutcome.Applied);
        (await _m.CashCountLines.CountAsync()).Should().Be(6);
        (await _m.CashShifts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task NonReplicatedEventType_IsIgnored_WithReceipt_AndNoTerminal()
    {
        var e = new SyncEnvelopeDto
        {
            Id = Guid.NewGuid(), EventType = "KitchenOrderCreated", AggregateType = "KitchenOrder", AggregateId = Guid.NewGuid(),
            OccurredAt = Now, TerminalCode = "CAJA-02", Payload = Json("{\"kitchenOrderId\":\"x\"}")
        };

        var r = await _inbox.ApplyAsync(e);

        r.Outcome.Should().Be(SyncInboxOutcome.Ignored);
        r.IsAcknowledged.Should().BeTrue("the satellite may mark it Synced");
        var receipt = await _m.SyncInboxReceipts.SingleAsync();
        receipt.MessageId.Should().Be(e.Id);
        receipt.Outcome.Should().Be(SyncInboxOutcome.Ignored);
        receipt.TerminalCode.Should().Be("CAJA-02");
        receipt.EventType.Should().Be("KitchenOrderCreated");
        receipt.AggregateId.Should().Be(e.AggregateId);
        receipt.ReceivedAt.Should().Be(Now);
        (await _m.Terminals.AnyAsync(t => t.Code == "CAJA-02")).Should().BeFalse("only replicated events register a sender");

        (await _inbox.ApplyAsync(e)).Outcome.Should().Be(SyncInboxOutcome.Duplicate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingTerminalCode_IsRejected_WithReceipt(string? code)
    {
        await RunSatelliteDayAsync();
        var e = Clone(await NewEnvelopeAsync(SyncEventTypes.OrderCompleted), x => x.TerminalCode = code);

        var r = await _inbox.ApplyAsync(e);

        r.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        r.IsAcknowledged.Should().BeFalse();
        r.Error.Should().Contain("terminalCode");
        var receipt = await _m.SyncInboxReceipts.SingleAsync();
        receipt.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        receipt.TerminalCode.Should().Be("DESCONOCIDA");
        receipt.Error.Should().Be(r.Error);
        (await _m.Orders.CountAsync()).Should().Be(0);
        (await _m.CashShifts.CountAsync()).Should().Be(0, "no placeholder shift is left behind");
        (await _m.Terminals.CountAsync()).Should().Be(1, "only the master itself");
    }

    [Fact]
    public async Task InvalidTerminalCode_IsRejected()
    {
        await RunSatelliteDayAsync();
        var e = Clone(await NewEnvelopeAsync(SyncEventTypes.OrderCompleted), x => x.TerminalCode = "X");
        (await _inbox.ApplyAsync(e)).Outcome.Should().Be(SyncInboxOutcome.Rejected);
    }

    [Fact]
    public async Task OwnTerminalCode_IsRejected_CaseInsensitive()
    {
        await RunSatelliteDayAsync();
        var e = Clone(await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened), x => x.TerminalCode = " caja-01 ");

        var r = await _inbox.ApplyAsync(e);

        r.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        r.Error.Should().Contain("CAJA-01");
        (await _m.SyncInboxReceipts.SingleAsync()).TerminalCode.Should().Be("DESCONOCIDA");
        (await _m.CashShifts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OnSatelliteContext_IsRejected_NothingWritten()
    {
        await RunSatelliteDayAsync();
        var e = await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened);
        var satInbox = new SyncInboxService(_m, _clock, Terminals.Satellite("CAJA-03"));

        var r = await satInbox.ApplyAsync(e);

        r.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        r.Error.Should().NotBeNullOrEmpty();
        (await _m.CashShifts.CountAsync()).Should().Be(0);
        (await _m.SyncInboxReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EmptyMessageId_IsRejected()
    {
        await RunSatelliteDayAsync();
        var e = Clone(await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened), x => x.Id = Guid.Empty);
        (await _inbox.ApplyAsync(e)).Outcome.Should().Be(SyncInboxOutcome.Rejected);
        (await _m.CashShifts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WithoutContext_ActsAsMaster()
    {
        await RunSatelliteDayAsync();
        var e = await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened);
        (await new SyncInboxService(_m, _clock).ApplyAsync(e)).Outcome.Should().Be(SyncInboxOutcome.Applied);
    }

    [Theory]
    [InlineData("OrderCompleted", "Order", "{\"orderId\":\"5b0c1b43-0000-0000-0000-000000000001\",\"total\":123}", "formato anterior")]  // legacy flat payload
    [InlineData("OrderCompleted", "Order", "\"not an object\"", "objeto")]
    [InlineData("OrderCompleted", "Order", "null", "objeto")]
    [InlineData("OrderCompleted", "Order", "{\"order\":{\"id\":\"00000000-0000-0000-0000-000000000000\",\"orderNumber\":\"X\",\"items\":[],\"payments\":[]}}", "order.id")]
    [InlineData("OrderCompleted", "Order", "{\"order\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"orderNumber\":\"\",\"items\":[],\"payments\":[]}}", "orderNumber")]
    [InlineData("OrderCompleted", "Order", "{\"order\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"orderNumber\":\"X\",\"payments\":[]}}", "items")]
    [InlineData("OrderCompleted", "Order", "{\"order\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"orderNumber\":\"X\",\"items\":[],\"payments\":[{\"id\":\"5b0c1b43-0000-0000-0000-000000000002\",\"method\":\"Bitcoin\",\"amount\":1}]}}", "JSON")]
    [InlineData("CashShiftOpened", "CashShift", "{\"shiftId\":\"5b0c1b43-0000-0000-0000-000000000001\",\"number\":1}", "shift")]
    [InlineData("CashShiftOpened", "CashShift", "{\"shift\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\"}}", "openedAt")]
    [InlineData("CashShiftClosed", "CashShift", "{\"shift\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"openedAt\":\"2026-11-12T12:00:00Z\"}}", "closedAt")]
    [InlineData("CashShiftClosed", "CashShift", "{\"shift\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"openedAt\":\"2026-11-12T12:00:00Z\",\"closedAt\":\"2026-11-12T20:00:00Z\",\"countLines\":[{\"denomination\":-1,\"quantity\":1}]}}", "arqueo")]
    [InlineData("CashMovementRecorded", "CashMovement", "{\"movement\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"cashShiftId\":\"5b0c1b43-0000-0000-0000-000000000009\",\"type\":\"PaidOut\",\"amount\":0}}", "amount")]
    [InlineData("CashMovementRecorded", "CashMovement", "{\"movement\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"cashShiftId\":\"5b0c1b43-0000-0000-0000-000000000009\",\"amount\":10}}", "type")]
    [InlineData("BillPaymentRecorded", "BillPayment", "{\"billPayment\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"cashShiftId\":\"5b0c1b43-0000-0000-0000-000000000009\",\"amount\":10,\"accountReference\":\"A\"}}", "receiptNumber")]
    [InlineData("BillPaymentRecorded", "BillPayment", "{\"billPayment\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"cashShiftId\":\"5b0c1b43-0000-0000-0000-000000000009\",\"amount\":10,\"receiptNumber\":\"REC-1\"}}", "accountReference")]
    public async Task MalformedPayload_IsRejected_WithReason_AndNoRows(string eventType, string aggregateType, string payload, string errorFragment)
    {
        var e = new SyncEnvelopeDto { Id = Guid.NewGuid(), EventType = eventType, AggregateType = aggregateType, AggregateId = Guid.NewGuid(), OccurredAt = Now, TerminalCode = "CAJA-02", Payload = Json(payload) };

        var r = await _inbox.ApplyAsync(e);

        r.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        r.Error.Should().ContainEquivalentOf(errorFragment);
        var receipt = await _m.SyncInboxReceipts.SingleAsync();
        receipt.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        receipt.TerminalCode.Should().Be("CAJA-02");
        receipt.Error.Should().NotBeNullOrEmpty();
        (await _m.Orders.CountAsync()).Should().Be(0);
        (await _m.CashShifts.CountAsync()).Should().Be(0, "a placeholder created before the validation failed must be discarded");
        (await _m.CashMovements.CountAsync()).Should().Be(0);
        (await _m.BillPayments.CountAsync()).Should().Be(0);
        (await _m.Terminals.AnyAsync(t => t.Code == "CAJA-02")).Should().BeFalse("a rejected message does not register the sender");
    }

    [Fact]
    public async Task RejectedMessage_LaterValid_BecomesApplied_ReceiptUpdatedInPlace()
    {
        await RunSatelliteDayAsync();
        var order = await NewEnvelopeAsync(SyncEventTypes.OrderCompleted);

        (await _inbox.ApplyAsync(Clone(order, x => x.TerminalCode = null))).Outcome.Should().Be(SyncInboxOutcome.Rejected);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await _inbox.ApplyAsync(order);

        retry.Outcome.Should().Be(SyncInboxOutcome.Applied);
        retry.Error.Should().BeNull();
        var receipt = await _m.SyncInboxReceipts.SingleAsync();
        receipt.MessageId.Should().Be(order.Id);
        receipt.Outcome.Should().Be(SyncInboxOutcome.Applied);
        receipt.Error.Should().BeNull();
        receipt.TerminalCode.Should().Be("CAJA-02");
        receipt.ReceivedAt.Should().Be(Now);
        receipt.UpdatedAt.Should().Be(Now);
        (await _m.Orders.CountAsync()).Should().Be(1);
    }

    // ── placeholder shift ────────────────────────────────────────────────────

    [Fact]
    public async Task OrderBeforeOpened_CreatesPlaceholderShift_FilledByOpenedAndClosed()
    {
        var day = await RunSatelliteDayAsync();
        var all = await NewEnvelopesAsync();
        var order = all.Single(e => e.EventType == SyncEventTypes.OrderCompleted);
        var opened = all.Single(e => e.EventType == SyncEventTypes.CashShiftOpened);
        var closed = all.Single(e => e.EventType == SyncEventTypes.CashShiftClosed);

        (await _inbox.ApplyAsync(order)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var placeholder = await _m.CashShifts.AsNoTracking().SingleAsync(s => s.Id == day.ShiftId);
        placeholder.CashierName.Should().Be("(sincronizando…)");
        placeholder.Status.Should().Be(CashShiftStatus.Open);
        placeholder.ShiftNumber.Should().Be(1, "taken from order.shiftNumber");
        placeholder.TerminalCode.Should().Be("CAJA-02");
        placeholder.TerminalId.Should().Be(_sat.Id);
        placeholder.OpeningAmount.Should().Be(0m);
        (await _m.Orders.SingleAsync()).CashShiftId.Should().Be(day.ShiftId);

        (await _inbox.ApplyAsync(opened)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        var filled = await _m.CashShifts.AsNoTracking().SingleAsync(s => s.Id == day.ShiftId);
        filled.CashierName.Should().Be("Ana");
        filled.OpeningAmount.Should().Be(100000m);
        filled.OpenedByUserId.Should().Be(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
        filled.Status.Should().Be(CashShiftStatus.Open);
        filled.ShiftNumber.Should().Be(1);

        (await _inbox.ApplyAsync(closed)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        var done = await _m.CashShifts.AsNoTracking().SingleAsync(s => s.Id == day.ShiftId);
        done.Status.Should().Be(CashShiftStatus.Closed);
        done.ClosingAmount.Should().Be(176500m);
        done.ExpectedAmount.Should().Be(176500m);
        (await _m.CashCountLines.CountAsync(l => l.CashShiftId == day.ShiftId)).Should().Be(6);
        (await _m.CashShifts.CountAsync()).Should().Be(1, "placeholder was reused, never duplicated");
    }

    [Fact]
    public async Task ClosedBeforeOpened_OpenedArrivingLate_DoesNotReopen()
    {
        var day = await RunSatelliteDayAsync();
        var all = await NewEnvelopesAsync();
        var opened = all.Single(e => e.EventType == SyncEventTypes.CashShiftOpened);
        var closed = all.Single(e => e.EventType == SyncEventTypes.CashShiftClosed);

        (await _inbox.ApplyAsync(closed)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        (await _inbox.ApplyAsync(opened)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var shift = await _m.CashShifts.AsNoTracking().SingleAsync(s => s.Id == day.ShiftId);
        shift.Status.Should().Be(CashShiftStatus.Closed);
        shift.CashierName.Should().Be("Ana");
        shift.ClosingAmount.Should().Be(176500m);
        (await new CashShiftService(_m, _clock, _settings, Terminals.Satellite("CAJA-02", id: _sat.Id)).GetCurrentShiftAsync()).Should().BeNull();
    }

    [Fact]
    public async Task BillPaymentAndMovementBeforeOpened_ShareOnePlaceholder_WithRealNumber()
    {
        var day = await RunSatelliteDayAsync();
        var all = await NewEnvelopesAsync();
        var bill = all.First(e => e.EventType == SyncEventTypes.BillPaymentRecorded);
        var movement = all.Single(e => e.EventType == SyncEventTypes.CashMovementRecorded);

        (await _inbox.ApplyAsync(bill)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        var placeholder = await _m.CashShifts.AsNoTracking().SingleAsync();
        placeholder.Id.Should().Be(day.ShiftId);
        placeholder.ShiftNumber.Should().BePositive("the bill-payment payload carries the top-level shiftNumber, so the placeholder gets the real number");
        placeholder.TerminalCode.Should().Be("CAJA-02");

        (await _inbox.ApplyAsync(movement)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        (await _m.CashShifts.CountAsync()).Should().Be(1);
        (await _m.CashMovements.SingleAsync()).CashShiftId.Should().Be(day.ShiftId);
        (await _m.BillPayments.SingleAsync()).CashShiftId.Should().Be(day.ShiftId);
    }

    [Fact]
    public async Task TwoPlaceholdersOfSameSatellite_DoNotCollideOnShiftNumber()
    {
        // Two bill payments of two different shifts (no shift number in the payload): placeholders 0 and -1.
        var first = new SyncEnvelopeDto { Id = Guid.NewGuid(), EventType = SyncEventTypes.BillPaymentRecorded, AggregateType = "BillPayment", AggregateId = Guid.NewGuid(), OccurredAt = Now, TerminalCode = "CAJA-02",
            Payload = Json("{\"billPayment\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000001\",\"cashShiftId\":\"5b0c1b43-0000-0000-0000-000000000011\",\"billerId\":\"5b0c1b43-0000-0000-0000-0000000000b1\",\"billerName\":\"E\",\"accountReference\":\"A\",\"amount\":10,\"commission\":0,\"method\":\"Cash\",\"receiptNumber\":\"REC-000001\",\"status\":\"Completed\",\"userName\":\"u\",\"paidAt\":\"2026-11-12T12:00:00Z\"}}") };
        var second = new SyncEnvelopeDto { Id = Guid.NewGuid(), EventType = SyncEventTypes.BillPaymentRecorded, AggregateType = "BillPayment", AggregateId = Guid.NewGuid(), OccurredAt = Now, TerminalCode = "CAJA-02",
            Payload = Json("{\"billPayment\":{\"id\":\"5b0c1b43-0000-0000-0000-000000000002\",\"cashShiftId\":\"5b0c1b43-0000-0000-0000-000000000012\",\"billerId\":\"5b0c1b43-0000-0000-0000-0000000000b1\",\"billerName\":\"E\",\"accountReference\":\"B\",\"amount\":10,\"commission\":0,\"method\":\"Cash\",\"receiptNumber\":\"REC-000002\",\"status\":\"Completed\",\"userName\":\"u\",\"paidAt\":\"2026-11-12T12:00:00Z\"}}") };

        (await _inbox.ApplyAsync(first)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        (await _inbox.ApplyAsync(second)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var numbers = await _m.CashShifts.Where(s => s.TerminalCode == "CAJA-02").Select(s => s.ShiftNumber).ToListAsync();
        numbers.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        numbers.Should().OnlyContain(n => n <= 0);
    }

    [Fact]
    public async Task ShiftOwnedByAnotherTerminal_IsRejected()
    {
        var day = await RunSatelliteDayAsync();
        var opened = await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened);
        _m.CashShifts.Add(new CashShift { Id = day.ShiftId, ShiftNumber = 9, CashierName = "Tres", Status = CashShiftStatus.Open, TerminalId = Guid.NewGuid(), TerminalCode = "CAJA-03" });
        await _m.SaveChangesAsync();

        var r = await _inbox.ApplyAsync(opened);

        r.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        r.Error.Should().Contain("CAJA-03");
        (await _m.CashShifts.AsNoTracking().SingleAsync()).CashierName.Should().Be("Tres", "untouched");
    }

    // ── collisions on unique indexes ─────────────────────────────────────────

    [Fact]
    public async Task ReceiptNumberCollision_IsQualifiedWithTerminalCode_AndKeptOnVoid()
    {
        var day = await RunSatelliteDayAsync();
        var all = await NewEnvelopesAsync();

        // The master already issued its own REC-000001 and REC-000002.
        var masterBiller = await _m.Billers.SingleAsync();
        var masterShifts = new CashShiftService(_m, _clock, _settings, _master);
        var masterShift = await masterShifts.OpenShiftAsync("Master", 100000m);
        var masterBills = new BillPaymentService(_m, _clock, _master);
        var mine1 = await masterBills.RecordAsync(new BillPaymentRequest(masterBiller.Id, "M-1", 1000m, PaymentMethod.Cash, 2500m, null, "Master"));
        var mine2 = await masterBills.RecordAsync(new BillPaymentRequest(masterBiller.Id, "M-2", 1000m, PaymentMethod.Cash, 2500m, null, "Master"));
        mine1.ReceiptNumber.Should().Be("REC-000001");
        mine2.ReceiptNumber.Should().Be("REC-000002");

        foreach (var e in all.Where(e => e.EventType is SyncEventTypes.BillPaymentRecorded or SyncEventTypes.BillPaymentVoided))
            (await _inbox.ApplyAsync(e)).Outcome.Should().Be(SyncInboxOutcome.Applied, e.EventType);

        var replicated = await _m.BillPayments.AsNoTracking().SingleAsync(b => b.Id == day.BillId);
        replicated.ReceiptNumber.Should().Be("CAJA-02:REC-000001");
        replicated.Amount.Should().Be(50000m);
        replicated.CashShiftId.Should().Be(day.ShiftId);
        var voided = await _m.BillPayments.AsNoTracking().SingleAsync(b => b.Id == day.VoidedBillId);
        voided.ReceiptNumber.Should().Be("CAJA-02:REC-000002", "the void event updates by id and must not rename the row");
        voided.Status.Should().Be(BillPaymentStatus.Voided);
        (await _m.BillPayments.SingleAsync(b => b.Id == mine1.Id)).ReceiptNumber.Should().Be("REC-000001", "the master's own rows are untouched");
        (await _m.BillPayments.CountAsync()).Should().Be(4);

        // The master's own reconciliation is unaffected by the replicated recaudos.
        (await masterShifts.GetReconciliationAsync(masterShift.Id)).BillPaymentCount.Should().Be(2);

        // And the master's next receipt number keeps its own sequence.
        var mine3 = await masterBills.RecordAsync(new BillPaymentRequest(masterBiller.Id, "M-3", 1000m, PaymentMethod.Cash, 2500m, null, "Master"));
        mine3.ReceiptNumber.Should().Be("REC-000003");
    }

    [Fact]
    public async Task OrderNumberCollision_IsQualifiedWithTerminalCode()
    {
        var day = await RunSatelliteDayAsync();
        var order = await NewEnvelopeAsync(SyncEventTypes.OrderCompleted);
        var satOrderNumber = (await _s.Orders.AsNoTracking().SingleAsync(o => o.Id == day.OrderId)).OrderNumber;
        _m.Orders.Add(new Order { OrderNumber = satOrderNumber, Status = OrderStatus.Completed, Total = 1m });
        await _m.SaveChangesAsync();

        (await _inbox.ApplyAsync(order)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var replicated = await _m.Orders.AsNoTracking().SingleAsync(o => o.Id == day.OrderId);
        replicated.OrderNumber.Should().Be($"CAJA-02:{satOrderNumber}");
        replicated.OrderNumber.Length.Should().BeLessThanOrEqualTo(50);
    }

    // ── sender bookkeeping ───────────────────────────────────────────────────

    [Fact]
    public async Task UnknownSender_AutoCreated_WithReportedId_LastSyncAtSet()
    {
        await RunSatelliteDayAsync();
        var opened = await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened);
        (await _m.Terminals.CountAsync()).Should().Be(1);

        (await _inbox.ApplyAsync(opened)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var t = await _m.Terminals.AsNoTracking().SingleAsync(x => x.Code == "CAJA-02");
        t.Id.Should().Be(_sat.Id);
        t.Kind.Should().Be(TerminalKind.Satellite);
        t.IsActive.Should().BeTrue();
        t.Name.Should().Be("CAJA-02");
        t.LastSyncAt.Should().Be(Now);
        t.LastSeenAt.Should().Be(Now);
        t.CreatedAt.Should().Be(Now);
        t.LastCatalogPullAt.Should().BeNull();
    }

    [Fact]
    public async Task UnknownSender_ReportedIdTaken_GetsNewId_RowsReferenceMastersTerminalRow()
    {
        await RunSatelliteDayAsync();
        var opened = await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened);
        // The satellite's id is already used by ANOTHER terminal on the master.
        _m.Terminals.Add(new Terminal { Id = _sat.Id, Code = "CAJA-09", Name = "Nueve", Kind = TerminalKind.Satellite });
        await _m.SaveChangesAsync();

        (await _inbox.ApplyAsync(opened)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        var t = await _m.Terminals.AsNoTracking().SingleAsync(x => x.Code == "CAJA-02");
        t.Id.Should().NotBe(_sat.Id);
        var shift = await _m.CashShifts.AsNoTracking().SingleAsync();
        shift.TerminalId.Should().Be(t.Id, "replicated rows reference the master's Terminal row, whatever id the satellite used");
        shift.TerminalCode.Should().Be("CAJA-02");
    }

    [Fact]
    public async Task KnownSender_FromHeartbeat_IsReused_AndLastSyncAtAdvances()
    {
        await RunSatelliteDayAsync();
        var opened = await NewEnvelopeAsync(SyncEventTypes.CashShiftOpened);
        var hb = await new TerminalService(_m, _clock, _master).HeartbeatAsync(new TerminalHeartbeatRequest("caja-02", "Caja dos", TerminalKind.Satellite, "1.0"), "10.0.0.2");
        hb.LastSyncAt.Should().BeNull();
        _clock.Advance(TimeSpan.FromMinutes(1));

        (await _inbox.ApplyAsync(opened)).Outcome.Should().Be(SyncInboxOutcome.Applied);

        (await _m.Terminals.CountAsync()).Should().Be(2);
        var t = await _m.Terminals.AsNoTracking().SingleAsync(x => x.Code == "CAJA-02");
        t.Id.Should().Be(hb.Id);
        t.Name.Should().Be("Caja dos", "the heartbeat's name is kept");
        t.LastSyncAt.Should().Be(Now);
        (await _m.CashShifts.SingleAsync()).TerminalId.Should().Be(hb.Id);
    }

    [Fact]
    public async Task IgnoredEvent_FromKnownSender_TouchesLastSeen_NotLastSync()
    {
        var hb = await new TerminalService(_m, _clock, _master).HeartbeatAsync(new TerminalHeartbeatRequest("CAJA-02", "Dos", TerminalKind.Satellite), null);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var e = new SyncEnvelopeDto { Id = Guid.NewGuid(), EventType = "KitchenOrderCreated", AggregateType = "KitchenOrder", AggregateId = Guid.NewGuid(), OccurredAt = Now, TerminalCode = "CAJA-02", Payload = Json("{}") };

        (await _inbox.ApplyAsync(e)).Outcome.Should().Be(SyncInboxOutcome.Ignored);

        var t = await _m.Terminals.AsNoTracking().SingleAsync(x => x.Id == hb.Id);
        t.LastSeenAt.Should().Be(Now);
        t.LastSyncAt.Should().BeNull();
    }

    [Fact]
    public async Task GetRecent_NewestFirst_Clamped()
    {
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _inbox.ApplyAsync(new SyncEnvelopeDto { Id = Guid.NewGuid(), EventType = "X", AggregateType = "X", AggregateId = Guid.NewGuid(), OccurredAt = Now, TerminalCode = "CAJA-02", Payload = Json("{}") });
        }
        var recent = await _inbox.GetRecentAsync(3);
        recent.Should().HaveCount(3);
        recent.Select(r => r.ReceivedAt).Should().BeInDescendingOrder();
        recent[0].ReceivedAt.Should().Be(Now);
        (await _inbox.GetRecentAsync(0)).Should().HaveCount(1, "take is clamped to at least 1");
    }
}
