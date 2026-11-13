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

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>
/// The terminal reference ("AUTH 123456 · VISA ****4242") and TerminalPaymentId travel from the charge to the sale /
/// recaudo, into the Outbox payload, through the sync inbox on the master, and into the shift reconciliation.
/// </summary>
public class PaymentReferencePlumbingTests : IDisposable
{
    private const string Ref = "AUTH 123456 · VISA ****4242";
    private readonly SqliteDb _sqlite = SqliteDb.Create();
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public PaymentReferencePlumbingTests() => _db = _sqlite.NewContext();

    public void Dispose()
    {
        _db.Dispose();
        _sqlite.Dispose();
    }

    private async Task<CashShift> OpenShiftAsync(PosDbContext db)
    {
        var shift = new CashShift { ShiftNumber = 1, CashierName = "Caja", Status = CashShiftStatus.Open, OpenedAt = _clock.GetUtcNow().UtcDateTime };
        db.CashShifts.Add(shift);
        await db.SaveChangesAsync();
        return shift;
    }

    private async Task<Product> AddProductAsync(PosDbContext db)
    {
        var p = new Product { Sku = "P1", Name = "Pan", Price = 10000m };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static JsonElement Payload(OutboxMessage m)
    {
        using var doc = JsonDocument.Parse(m.PayloadJson);
        return doc.RootElement.Clone();
    }

    // ── SalesService ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SalesService_CardWithReference_StoresReferenceAndTerminalPaymentId_AndOrderDtoExposesThem()
    {
        var shift = await OpenShiftAsync(_db);
        var product = await AddProductAsync(_db);
        var sales = new SalesService(_db);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 2);
        var terminalPaymentId = Guid.NewGuid();

        var result = await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Card, order.Total, order.Total, $"  {Ref}  ", terminalPaymentId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var payment = await _db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == order.Id);
        payment.Method.Should().Be(PaymentMethod.Card);
        payment.Reference.Should().Be(Ref, "trimmed");
        payment.TerminalPaymentId.Should().Be(terminalPaymentId);

        var dto = await sales.GetByIdAsync(order.Id);
        dto!.PaymentReference.Should().Be(Ref);
        dto.TerminalPaymentId.Should().Be(terminalPaymentId);
    }

    [Fact]
    public async Task SalesService_LegacyOverload_AndCash_LeaveReferenceNull()
    {
        var shift = await OpenShiftAsync(_db);
        var product = await AddProductAsync(_db);
        var sales = new SalesService(_db);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 1);

        (await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Cash, order.Total, 20000m)).Success.Should().BeTrue();

        var dto = await sales.GetByIdAsync(order.Id);
        dto!.PaymentReference.Should().BeNull();
        dto.TerminalPaymentId.Should().BeNull();

        var o2 = await sales.CreateOrderAsync(null, shift.Id);
        o2 = await sales.AddItemAsync(o2.Id, product.Id, 1);
        (await sales.ProcessPaymentAsync(o2.Id, PaymentMethod.Card, o2.Total, o2.Total, "   ", null)).Success.Should().BeTrue();
        (await sales.GetByIdAsync(o2.Id))!.PaymentReference.Should().BeNull("blank references are not stored");
    }

    [Fact]
    public async Task Outbox_OrderCompleted_CarriesReferenceAndTerminalPaymentIdPerPayment()
    {
        var shift = await OpenShiftAsync(_db);
        var product = await AddProductAsync(_db);
        var sales = new SalesService(_db);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 1);
        var terminalPaymentId = Guid.NewGuid();
        await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Card, order.Total, order.Total, Ref, terminalPaymentId);

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventType == SyncEventTypes.OrderCompleted);
        var payments = Payload(msg).GetProperty("order").GetProperty("payments");

        payments.GetArrayLength().Should().Be(1);
        var p = payments[0];
        p.GetProperty("terminalPaymentId").GetGuid().Should().Be(terminalPaymentId);
        p.GetProperty("reference").GetString().Should().Be(Ref);
        p.GetProperty("method").GetString().Should().Be("Card");
        Payload(msg).GetProperty("paymentMethod").GetString().Should().Be("Card");
    }

    [Fact]
    public async Task Outbox_OrderCompleted_CashPayment_HasNullTerminalPaymentId()
    {
        var shift = await OpenShiftAsync(_db);
        var product = await AddProductAsync(_db);
        var sales = new SalesService(_db);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 1);
        await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Cash, order.Total, order.Total);

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventType == SyncEventTypes.OrderCompleted);
        var p = Payload(msg).GetProperty("order").GetProperty("payments")[0];
        p.GetProperty("terminalPaymentId").ValueKind.Should().Be(JsonValueKind.Null);
        p.GetProperty("reference").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── BillPaymentService ───────────────────────────────────────────────────

    [Fact]
    public async Task BillPayment_Card_StoresPaymentReferenceAndTerminalPaymentId_AndOutboxCarriesThem()
    {
        var shifts = new CashShiftService(_db, _clock);
        await shifts.OpenShiftAsync("Ana", 100000m);
        var biller = await CashOpsData.AddBillerAsync(_db, "Energia", 1500m);
        var bills = new BillPaymentService(_db, _clock);
        var terminalPaymentId = Guid.NewGuid();

        var dto = await bills.RecordAsync(new BillPaymentRequest(biller.Id, "CTA-1", 50000m, PaymentMethod.Card, 0m, null, "Ana", null, $" {Ref} ", terminalPaymentId));

        dto.PaymentReference.Should().Be(Ref);
        var row = await _db.BillPayments.AsNoTracking().SingleAsync(b => b.Id == dto.Id);
        row.PaymentReference.Should().Be(Ref);
        row.TerminalPaymentId.Should().Be(terminalPaymentId);
        (await bills.GetForShiftAsync()).Single(b => b.Id == dto.Id).PaymentReference.Should().Be(Ref);

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventType == SyncEventTypes.BillPaymentRecorded);
        var payload = Payload(msg);
        payload.GetProperty("billPayment").GetProperty("paymentReference").GetString().Should().Be(Ref);
        payload.GetProperty("billPayment").GetProperty("terminalPaymentId").GetGuid().Should().Be(terminalPaymentId);
        payload.GetProperty("paymentReference").GetString().Should().Be(Ref, "legacy flat key");
    }

    [Fact]
    public async Task BillPayment_Cash_IgnoresPaymentReference()
    {
        var shifts = new CashShiftService(_db, _clock);
        await shifts.OpenShiftAsync("Ana", 100000m);
        var biller = await CashOpsData.AddBillerAsync(_db, "Energia", 1500m);
        var bills = new BillPaymentService(_db, _clock);

        var dto = await bills.RecordAsync(new BillPaymentRequest(biller.Id, "CTA-1", 50000m, PaymentMethod.Cash, 60000m, null, "Ana", null, Ref, null));

        dto.PaymentReference.Should().BeNull("a cash recaudo has no card reference");
        (await _db.BillPayments.AsNoTracking().SingleAsync(b => b.Id == dto.Id)).PaymentReference.Should().BeNull();
    }

    [Fact]
    public async Task BillPaymentReceipt_PrintsTheCardReference()
    {
        var shifts = new CashShiftService(_db, _clock);
        await shifts.OpenShiftAsync("Ana", 100000m);
        var biller = await CashOpsData.AddBillerAsync(_db, "Energia", 1500m);
        var bills = new BillPaymentService(_db, _clock);
        var dto = await bills.RecordAsync(new BillPaymentRequest(biller.Id, "CTA-1", 50000m, PaymentMethod.Card, 0m, null, "Ana", null, Ref, Guid.NewGuid()));

        var lines = Core.Application.Receipts.CashReports.BillPaymentReceipt(dto, "Tienda");

        lines.Select(l => l.Text).Should().Contain("Ref: AUTH 123456 - VISA ****4242");
    }

    // ── reconciliation ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_TerminalCardTotal_SumsOnlyApprovedChargesOfThatShift()
    {
        var shifts = new CashShiftService(_db, _clock, new CashShiftSettings { ManagerRequiredFor = new List<string>() });
        var shiftA = await shifts.OpenShiftAsync("Ana", 100000m);
        var shiftB = new CashShift { ShiftNumber = 2, CashierName = "Otro", Status = CashShiftStatus.Closed, OpenedAt = DateTime.UtcNow, ClosedAt = DateTime.UtcNow };
        var terminal = new PaymentTerminal { Name = "Datafono", Provider = PaymentTerminalProvider.Mock };
        _db.AddRange(shiftB, terminal);
        await _db.SaveChangesAsync();

        // The card sales themselves (what the terminal charges are a subset of).
        CashOpsData.AddSale(_db, shiftA.Id, PaymentMethod.Card, 30000m);
        CashOpsData.AddSale(_db, shiftA.Id, PaymentMethod.Card, 12000m);

        TerminalPayment Row(Guid? shiftId, TerminalPaymentStatus status, decimal amount) => new()
        {
            PaymentTerminalId = terminal.Id, TerminalName = terminal.Name, Purpose = TerminalPaymentPurpose.Sale, Amount = amount, Currency = "COP",
            Status = status, RequestedByName = "Ana", RequestedAt = DateTime.UtcNow, CashShiftId = shiftId
        };
        _db.TerminalPayments.AddRange(
            Row(shiftA.Id, TerminalPaymentStatus.Approved, 10000m),
            Row(shiftA.Id, TerminalPaymentStatus.Approved, 20000m),
            Row(shiftA.Id, TerminalPaymentStatus.Declined, 5000m),
            Row(shiftA.Id, TerminalPaymentStatus.Pending, 7000m),
            Row(shiftA.Id, TerminalPaymentStatus.Timeout, 7000m),
            Row(shiftA.Id, TerminalPaymentStatus.Cancelled, 7000m),
            Row(shiftA.Id, TerminalPaymentStatus.Error, 7000m),
            Row(shiftB.Id, TerminalPaymentStatus.Approved, 999m),
            Row(null, TerminalPaymentStatus.Approved, 555m));
        await _db.SaveChangesAsync();

        var r = await shifts.GetReconciliationAsync(shiftA.Id);

        r.TerminalCardTotal.Should().Be(30000m);
        r.TerminalChargeCount.Should().Be(2);
        r.CardSales.Should().Be(42000m, "terminal totals are informative; card sales still come from the orders");
        r.ExpectedCard.Should().Be(42000m);

        var rb = await shifts.GetReconciliationAsync(shiftB.Id);
        rb.TerminalCardTotal.Should().Be(999m);
        rb.TerminalChargeCount.Should().Be(1);

        var z = Core.Application.Receipts.CashReports.ZReport(r, "Tienda");
        z.Select(l => l.Text).Should().Contain(l => l.StartsWith("  por datafono:") && l.Contains("$30.000") && l.Contains("(2)"));
    }

    [Fact]
    public async Task Reconciliation_WithoutTerminalCharges_IsZero_AndZReportOmitsTheLine()
    {
        var shifts = new CashShiftService(_db, _clock, new CashShiftSettings { ManagerRequiredFor = new List<string>() });
        var shift = await shifts.OpenShiftAsync("Ana", 100000m);
        CashOpsData.AddSale(_db, shift.Id, PaymentMethod.Card, 30000m);

        var r = await shifts.GetReconciliationAsync(shift.Id);

        r.TerminalCardTotal.Should().Be(0m);
        r.TerminalChargeCount.Should().Be(0);
        Core.Application.Receipts.CashReports.ZReport(r, "Tienda").Select(l => l.Text).Should().NotContain(l => l.Contains("datafono"));
    }

    // ── sync inbox (satellite → master) ──────────────────────────────────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task SyncInbox_MasterAppliesReferenceAndTerminalPaymentId_ForOrderAndBillPayment()
    {
        using var masterDb = SqliteDb.Create();
        await using var m = masterDb.NewContext();
        var master = Terminals.Master("CAJA-01");
        var sat = Terminals.Satellite("CAJA-02");
        await new TerminalService(m, _clock, master).EnsureRegisteredAsync(master.Code, master.Name, TerminalKind.Master);

        // Catalog created on the master and pulled by the satellite (ids preserved), as in production.
        var product = new Product { Sku = "PAN-1", Name = "Pan", Price = 10000m };
        var biller = new Biller { Name = "Energia", CommissionFixed = 1500m, SortOrder = 1 };
        m.AddRange(product, biller);
        await m.SaveChangesAsync();
        var snap = await new CatalogSyncService(m, _clock, master).BuildSnapshotAsync();
        await new CatalogSyncService(_db, _clock, sat).ApplySnapshotAsync(snap);

        // Satellite: card sale and card recaudo, both through a datáfono.
        var satShifts = new CashShiftService(_db, _clock, new CashShiftSettings { ManagerRequiredFor = new List<string>() }, sat);
        var shift = await satShifts.OpenShiftAsync("Ana", 100000m);
        var sales = new SalesService(_db, sat);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 1);
        var orderChargeId = Guid.NewGuid();
        (await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Card, order.Total, order.Total, Ref, orderChargeId)).Success.Should().BeTrue();
        var billChargeId = Guid.NewGuid();
        var bill = await new BillPaymentService(_db, _clock, sat).RecordAsync(
            new BillPaymentRequest(biller.Id, "CTA-1", 50000m, PaymentMethod.Card, 0m, null, "Ana", null, "AUTH 999999 · AMEX ****0005", billChargeId));
        _db.ChangeTracker.Clear();

        // Envelopes exactly as the real sink would POST them.
        var handler = new CaptureHandler();
        var sink = new HttpOutboxSink(new HttpClient(handler), new SyncSettings { Enabled = true, WebhookUrl = "http://master/api/sync/inbox" }, sat);
        var envelopes = new List<SyncEnvelopeDto>();
        foreach (var msg in await _db.OutboxMessages.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync())
        {
            await sink.DeliverAsync(msg);
            envelopes.Add(JsonSerializer.Deserialize<SyncEnvelopeDto>(handler.Body!, Web)!);
        }
        envelopes.Select(e => e.EventType).Should().Contain(new[] { SyncEventTypes.OrderCompleted, SyncEventTypes.BillPaymentRecorded });

        var inbox = new SyncInboxService(m, _clock, master);
        foreach (var e in envelopes.OrderBy(e => e.EventType == SyncEventTypes.CashShiftOpened ? 0 : 1))
            (await inbox.ApplyAsync(e)).Outcome.Should().Be(SyncInboxOutcome.Applied, e.EventType);

        var mOrder = await m.Orders.AsNoTracking().Include(o => o.Payments).SingleAsync(o => o.Id == order.Id);
        var mPayment = mOrder.Payments.Should().ContainSingle().Subject;
        mPayment.Method.Should().Be(PaymentMethod.Card);
        mPayment.Reference.Should().Be(Ref);
        mPayment.TerminalPaymentId.Should().Be(orderChargeId);

        var mBill = await m.BillPayments.AsNoTracking().SingleAsync(b => b.Id == bill.Id);
        mBill.Method.Should().Be(PaymentMethod.Card);
        mBill.PaymentReference.Should().Be("AUTH 999999 · AMEX ****0005");
        mBill.TerminalPaymentId.Should().Be(billChargeId);

        // The master's own OrderDto / reconciliation see the reference too.
        (await new SalesService(m, master).GetByIdAsync(order.Id))!.PaymentReference.Should().Be(Ref);
    }
}
