using System.Text.Json;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static Loqzen.Tests.Fixtures.CashOpsData;

namespace Loqzen.Tests.Services;

/// <summary>
/// Two ITerminalContexts over the SAME real SQLite database (the master's, which also holds the satellites'
/// replicated shifts): each register sees only its own open shift, numbering, history and float; legacy rows
/// belong to the master. Plus the Outbox payload contracts every writer must honour (SyncEventTypes).
/// </summary>
public class CashShiftTerminalScopingTests : IDisposable
{
    private readonly SqliteDb _sqlite = SqliteDb.Create();
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly CashShiftSettings _settings = new() { ManagerRequiredFor = new List<string>() };
    private readonly TerminalContext _master = Terminals.Master("CAJA-01");
    private readonly TerminalContext _sat = Terminals.Satellite("CAJA-02");
    private readonly CashShiftService _m;
    private readonly CashShiftService _s;

    public CashShiftTerminalScopingTests()
    {
        _db = _sqlite.NewContext();
        _m = new CashShiftService(_db, _clock, _settings, _master);
        _s = new CashShiftService(_db, _clock, _settings, _sat);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqlite.Dispose();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<JsonElement> LastPayloadAsync(string eventType, Guid aggregateId)
    {
        var msg = await _db.OutboxMessages.AsNoTracking()
            .Where(m => m.EventType == eventType && m.AggregateId == aggregateId)
            .OrderByDescending(m => m.CreatedAt).FirstAsync();
        using var doc = JsonDocument.Parse(msg.PayloadJson);
        return doc.RootElement.Clone();
    }

    private CashShift LegacyShift(int number, CashShiftStatus status, decimal floatLeft = 0m, string? closedBy = null)
    {
        var s = new CashShift
        {
            ShiftNumber = number, CashierName = "Legacy", Status = status, OpenedAt = _clock.GetUtcNow().UtcDateTime.AddDays(-1),
            ClosedAt = status == CashShiftStatus.Closed ? _clock.GetUtcNow().UtcDateTime.AddDays(-1).AddHours(8) : null,
            FloatLeftAmount = floatLeft, ClosedByName = closedBy
        };
        _db.CashShifts.Add(s);
        _db.SaveChanges();
        return s;
    }

    // ── scoping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachTerminal_HasItsOwnOpenShift()
    {
        var ms = await _m.OpenShiftAsync("Ana", 100000m);
        var ss = await _s.OpenShiftAsync("Luis", 50000m);

        ms.TerminalId.Should().Be(_master.Id);
        ms.TerminalCode.Should().Be("CAJA-01");
        ss.TerminalId.Should().Be(_sat.Id);
        ss.TerminalCode.Should().Be("CAJA-02");

        (await _m.GetCurrentShiftAsync())!.Id.Should().Be(ms.Id);
        (await _s.GetCurrentShiftAsync())!.Id.Should().Be(ss.Id);

        var again = () => _m.OpenShiftAsync("Otro", 0m);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ana*");

        // Without a context the whole table is visible: "the open shift" is ambiguous, first match wins.
        (await new CashShiftService(_db, _clock, _settings).GetCurrentShiftAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task ShiftNumbers_AreSequencedPerTerminal_MasterContinuesLegacySequence()
    {
        LegacyShift(3, CashShiftStatus.Closed);

        var m1 = await _m.OpenShiftAsync("Ana", 0m);
        var s1 = await _s.OpenShiftAsync("Luis", 0m);
        m1.ShiftNumber.Should().Be(4, "legacy rows count as the master's");
        s1.ShiftNumber.Should().Be(1, "a satellite starts its own sequence");

        await _m.CloseShiftAsync(Close(0m));
        await _s.CloseShiftAsync(Close(0m));
        _clock.Advance(TimeSpan.FromMinutes(1));

        (await _m.OpenShiftAsync("Ana", 0m)).ShiftNumber.Should().Be(5);
        (await _s.OpenShiftAsync("Luis", 0m)).ShiftNumber.Should().Be(2);

        // (TerminalCode, ShiftNumber) unique index held: CAJA-01 #1..#5 and CAJA-02 #1..#2 coexist.
        (await _db.CashShifts.CountAsync(s => s.ShiftNumber == 1)).Should().Be(1);
        (await _db.CashShifts.CountAsync(s => s.ShiftNumber == 2)).Should().Be(1);
    }

    [Fact]
    public async Task History_And_SuggestedFloat_AreScopedPerTerminal()
    {
        await _m.OpenShiftAsync("Ana", 150000m);
        await _s.OpenShiftAsync("Luis", 50000m);
        await _m.CloseShiftAsync(Close(150000m, floatLeft: 150000m));
        await _s.CloseShiftAsync(Close(50000m, floatLeft: 50000m));

        var mh = await _m.GetHistoryAsync();
        var sh = await _s.GetHistoryAsync();
        mh.Should().ContainSingle().Which.TerminalCode.Should().Be("CAJA-01");
        sh.Should().ContainSingle().Which.TerminalCode.Should().Be("CAJA-02");

        (await _m.GetSuggestedOpeningAmountAsync()).Should().Be(150000m);
        (await _s.GetSuggestedOpeningAmountAsync()).Should().Be(50000m);

        var unscoped = new CashShiftService(_db, _clock, _settings);
        (await unscoped.GetHistoryAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task SuggestedFloat_Satellite_UsesDefault_WhenOnlyMasterClosedShifts()
    {
        await _m.OpenShiftAsync("Ana", 150000m);
        await _m.CloseShiftAsync(Close(150000m, floatLeft: 150000m));
        (await _s.GetSuggestedOpeningAmountAsync()).Should().Be(_settings.DefaultFloat);
    }

    [Fact]
    public async Task LegacyNullRows_AreVisibleToMasterOnly()
    {
        var legacyOpen = LegacyShift(2, CashShiftStatus.Open);
        LegacyShift(1, CashShiftStatus.Closed, floatLeft: 77000m, closedBy: "Viejo");

        (await _m.GetCurrentShiftAsync())!.Id.Should().Be(legacyOpen.Id, "the master owns rows created before multi-register");
        (await _s.GetCurrentShiftAsync()).Should().BeNull();

        var masterOpen = () => _m.OpenShiftAsync("Ana", 0m);
        await masterOpen.Should().ThrowAsync<InvalidOperationException>("the legacy shift is the master's open shift");
        var sat = await _s.OpenShiftAsync("Luis", 0m);
        sat.ShiftNumber.Should().Be(1);

        (await _m.GetHistoryAsync()).Should().ContainSingle(h => h.TerminalCode == null);
        (await _s.GetHistoryAsync()).Should().BeEmpty();

        (await _m.GetSuggestedOpeningAmountAsync()).Should().Be(77000m, "legacy closed shift of the master");
        (await _s.GetSuggestedOpeningAmountAsync()).Should().Be(_settings.DefaultFloat);

        // GetReconciliationAsync() without id resolves the open shift of the caller's terminal.
        (await _m.GetReconciliationAsync()).ShiftId.Should().Be(legacyOpen.Id);
        (await _s.GetReconciliationAsync()).ShiftId.Should().Be(sat.Id);
    }

    [Fact]
    public async Task Movements_PartialCounts_AndBillPayments_GoToTheCallersShift()
    {
        var ms = await _m.OpenShiftAsync("Ana", 100000m);
        var ss = await _s.OpenShiftAsync("Luis", 50000m);
        var biller = await AddBillerAsync(_db, "Energia", fixedCommission: 1500m);

        await _s.RecordMovementAsync(Movement(CashMovementType.PaidOut, 5000m, "Hielo"));
        await _s.RecordPartialCountAsync(Count(45000m), "Luis");
        var bill = await new BillPaymentService(_db, _clock, _sat).RecordAsync(new BillPaymentRequest(biller.Id, "REF-1", 20000m, PaymentMethod.Cash, 21500m, null, "Luis"));

        (await _db.CashMovements.SingleAsync(m => m.Type == CashMovementType.PaidOut)).CashShiftId.Should().Be(ss.Id);
        (await _db.PartialCashCounts.SingleAsync()).CashShiftId.Should().Be(ss.Id);
        var row = await _db.BillPayments.SingleAsync(b => b.Id == bill.Id);
        row.CashShiftId.Should().Be(ss.Id);
        row.TerminalId.Should().Be(_sat.Id);
        row.TerminalCode.Should().Be("CAJA-02");

        (await _m.GetReconciliationAsync()).ExpectedCash.Should().Be(100000m, "nothing happened on the master's shift");
        (await _s.GetReconciliationAsync()).ExpectedCash.Should().Be(50000m - 5000m + 21500m);
        (await new BillPaymentService(_db, _clock, _master).GetForShiftAsync()).Should().BeEmpty();
        (await new BillPaymentService(_db, _clock, _sat).GetForShiftAsync()).Should().ContainSingle();
        ms.Id.Should().NotBe(ss.Id);
    }

    [Fact]
    public async Task SalesService_StampsOrdersWithTerminal()
    {
        var product = new Product { Sku = "P1", Name = "Pan", Price = 1000m };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        var ss = await _s.OpenShiftAsync("Luis", 0m);

        var sales = new SalesService(_db, _sat);
        var order = await sales.CreateOrderAsync(null, ss.Id);
        await sales.AddItemAsync(order.Id, product.Id, 2);
        (await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Card, 2000m, 2000m)).Success.Should().BeTrue();

        var row = await _db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        row.TerminalId.Should().Be(_sat.Id);
        row.TerminalCode.Should().Be("CAJA-02");
        (await _s.GetReconciliationAsync()).CardSales.Should().Be(2000m);
    }

    // ── Outbox payload contracts ─────────────────────────────────────────────

    [Fact]
    public async Task OpenShift_WritesCashShiftOpened_WithNestedShift()
    {
        var shift = await _s.OpenShiftAsync("Luis", 50000m, Guid.Parse("00000000-0000-0000-0000-00000000aaaa"));

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventType == SyncEventTypes.CashShiftOpened);
        msg.AggregateType.Should().Be("CashShift");
        msg.AggregateId.Should().Be(shift.Id);
        msg.Status.Should().Be(SyncStatus.Pending);
        msg.CreatedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);

        var p = await LastPayloadAsync(SyncEventTypes.CashShiftOpened, shift.Id);
        var s = p.GetProperty("shift");
        s.GetProperty("id").GetGuid().Should().Be(shift.Id);
        s.GetProperty("shiftNumber").GetInt32().Should().Be(1);
        s.GetProperty("cashierName").GetString().Should().Be("Luis");
        s.GetProperty("openingAmount").GetDecimal().Should().Be(50000m);
        s.GetProperty("openedAt").GetDateTime().Should().Be(_clock.GetUtcNow().UtcDateTime);
        s.GetProperty("openedByUserId").GetGuid().Should().Be(Guid.Parse("00000000-0000-0000-0000-00000000aaaa"));
        s.GetProperty("terminalId").GetGuid().Should().Be(_sat.Id);
        s.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        s.GetProperty("status").GetString().Should().Be("Open", "enums travel as names");
        s.TryGetProperty("countLines", out _).Should().BeFalse("count lines only exist on the closed event");
        p.GetProperty("shiftId").GetGuid().Should().Be(shift.Id, "legacy flat keys are kept");
    }

    [Fact]
    public async Task CloseShift_WritesCashShiftClosed_WithCountLines_AndCloseFields()
    {
        var shift = await _s.OpenShiftAsync("Luis", 50000m);
        AddSale(_db, shift.Id, PaymentMethod.Cash, 30000m);
        _clock.Advance(TimeSpan.FromHours(8));
        var lines = new[]
        {
            new CashCountLineDto { Denomination = 50000m, Quantity = 1 },
            new CashCountLineDto { Denomination = 20000m, Quantity = 1 },
            new CashCountLineDto { Denomination = 10000m, Quantity = 1 },
            new CashCountLineDto { Denomination = 1000m, Quantity = 0 }, // ignored
        };
        var closedBy = Guid.NewGuid();
        await _s.CloseShiftAsync(new CloseShiftRequest(lines, 20000m, "Todo bien", closedBy, "Supervisor", true));

        var p = await LastPayloadAsync(SyncEventTypes.CashShiftClosed, shift.Id);
        var s = p.GetProperty("shift");
        s.GetProperty("id").GetGuid().Should().Be(shift.Id);
        s.GetProperty("status").GetString().Should().Be("Closed");
        s.GetProperty("openingAmount").GetDecimal().Should().Be(50000m);
        s.GetProperty("closedAt").GetDateTime().Should().Be(_clock.GetUtcNow().UtcDateTime);
        s.GetProperty("closingAmount").GetDecimal().Should().Be(80000m);
        s.GetProperty("expectedAmount").GetDecimal().Should().Be(80000m);
        s.GetProperty("floatLeftAmount").GetDecimal().Should().Be(20000m);
        s.GetProperty("wasBlindCount").GetBoolean().Should().BeTrue();
        s.GetProperty("notes").GetString().Should().Be("Todo bien");
        s.GetProperty("closedByUserId").GetGuid().Should().Be(closedBy);
        s.GetProperty("closedByName").GetString().Should().Be("Supervisor");
        s.GetProperty("differenceOverrideId").ValueKind.Should().Be(JsonValueKind.Null);
        s.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");

        var count = s.GetProperty("countLines").EnumerateArray().ToList();
        count.Should().HaveCount(3, "zero-quantity lines are dropped");
        count.Select(l => (l.GetProperty("denomination").GetDecimal(), l.GetProperty("quantity").GetInt32()))
            .Should().BeEquivalentTo(new[] { (50000m, 1), (20000m, 1), (10000m, 1) });

        p.GetProperty("counted").GetDecimal().Should().Be(80000m, "legacy flat keys");
        p.GetProperty("difference").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task LegacyCloseOverloads_WriteCashShiftClosed_WithCountLinesArray()
    {
        var shift = await _s.OpenShiftAsync("Luis", 10000m);
        await _s.CloseShiftAsync(10000m, "legacy");
        var p = await LastPayloadAsync(SyncEventTypes.CashShiftClosed, shift.Id);
        p.GetProperty("shift").GetProperty("countLines").ValueKind.Should().Be(JsonValueKind.Array);
        p.GetProperty("shift").GetProperty("countLines").GetArrayLength().Should().Be(0);
        p.GetProperty("shift").GetProperty("status").GetString().Should().Be("Closed");

        _clock.Advance(TimeSpan.FromMinutes(1));
        var shift2 = await _s.OpenShiftAsync("Luis", 10000m);
        await _s.CloseShiftWithCountAsync(new[] { new CashCountLineDto { Denomination = 10000m, Quantity = 1 } });
        var p2 = await LastPayloadAsync(SyncEventTypes.CashShiftClosed, shift2.Id);
        p2.GetProperty("shift").GetProperty("countLines").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RecordMovement_WritesCashMovementRecorded_WithNestedMovement()
    {
        var shift = await _s.OpenShiftAsync("Luis", 50000m);
        var dto = await _s.RecordMovementAsync(Movement(CashMovementType.Expense, 3000m, "Bolsas", reference: "FAC-9"));

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventType == SyncEventTypes.CashMovementRecorded);
        msg.AggregateType.Should().Be("CashMovement");
        msg.AggregateId.Should().Be(dto.Id);
        var p = await LastPayloadAsync(SyncEventTypes.CashMovementRecorded, dto.Id);
        var m = p.GetProperty("movement");
        m.GetProperty("id").GetGuid().Should().Be(dto.Id);
        m.GetProperty("cashShiftId").GetGuid().Should().Be(shift.Id);
        m.GetProperty("type").GetString().Should().Be("Expense");
        m.GetProperty("amount").GetDecimal().Should().Be(3000m);
        m.GetProperty("reason").GetString().Should().Be("Bolsas");
        m.GetProperty("reference").GetString().Should().Be("FAC-9");
        m.GetProperty("userName").GetString().Should().Be("Cajero");
        m.GetProperty("occurredAt").GetDateTime().Should().Be(_clock.GetUtcNow().UtcDateTime);
        p.GetProperty("shiftNumber").GetInt32().Should().Be(1, "top-level shiftNumber lets the master number a placeholder shift");
        p.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
    }

    [Fact]
    public async Task OrderCompleted_Payload_HasNestedOrder_WithItemsAndPayments()
    {
        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        var product = new Product { Sku = "P1", Name = "Pan", Price = 1000m, Tax = tax };
        _db.AddRange(tax, product);
        await _db.SaveChangesAsync();
        var shift = await _s.OpenShiftAsync("Luis", 0m);

        var sales = new SalesService(_db, _sat);
        var order = await sales.CreateOrderAsync(null, shift.Id);
        order = await sales.AddItemAsync(order.Id, product.Id, 3);
        (await sales.ProcessPaymentAsync(order.Id, PaymentMethod.Cash, order.Total, 5000m)).Success.Should().BeTrue();

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventType == SyncEventTypes.OrderCompleted);
        msg.AggregateType.Should().Be("Order");
        msg.AggregateId.Should().Be(order.Id);

        var p = await LastPayloadAsync(SyncEventTypes.OrderCompleted, order.Id);
        var o = p.GetProperty("order");
        o.GetProperty("id").GetGuid().Should().Be(order.Id);
        o.GetProperty("orderNumber").GetString().Should().Be(order.OrderNumber);
        o.GetProperty("status").GetString().Should().Be("Completed");
        o.GetProperty("subtotal").GetDecimal().Should().Be(3000m);
        o.GetProperty("taxTotal").GetDecimal().Should().Be(570m);
        o.GetProperty("discountTotal").GetDecimal().Should().Be(0m);
        o.GetProperty("total").GetDecimal().Should().Be(3570m);
        o.GetProperty("cashShiftId").GetGuid().Should().Be(shift.Id);
        o.GetProperty("shiftNumber").GetInt32().Should().Be(1);
        o.GetProperty("terminalId").GetGuid().Should().Be(_sat.Id);
        o.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        o.GetProperty("customerId").ValueKind.Should().Be(JsonValueKind.Null);
        o.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.String);
        o.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.String);

        var items = o.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        var i = items[0];
        i.GetProperty("id").GetGuid().Should().NotBeEmpty();
        i.GetProperty("productId").GetGuid().Should().Be(product.Id);
        i.GetProperty("productName").GetString().Should().Be("Pan");
        i.GetProperty("unitPrice").GetDecimal().Should().Be(1000m);
        i.GetProperty("quantity").GetDecimal().Should().Be(3m);
        i.GetProperty("weightKg").ValueKind.Should().Be(JsonValueKind.Null);
        i.GetProperty("taxRate").GetDecimal().Should().Be(0.19m);
        i.GetProperty("taxAmount").GetDecimal().Should().Be(570m);
        i.GetProperty("subtotal").GetDecimal().Should().Be(3000m);
        i.GetProperty("total").GetDecimal().Should().Be(3570m);

        var pays = o.GetProperty("payments").EnumerateArray().ToList();
        pays.Should().ContainSingle();
        var pay = pays[0];
        pay.GetProperty("id").GetGuid().Should().NotBeEmpty();
        pay.GetProperty("method").GetString().Should().Be("Cash");
        pay.GetProperty("amount").GetDecimal().Should().Be(3570m);
        pay.GetProperty("tenderedAmount").GetDecimal().Should().Be(5000m);
        pay.GetProperty("changeAmount").GetDecimal().Should().Be(1430m);
        pay.GetProperty("reference").ValueKind.Should().Be(JsonValueKind.Null);
        pay.GetProperty("processedAt").ValueKind.Should().Be(JsonValueKind.String);

        p.GetProperty("orderId").GetGuid().Should().Be(order.Id, "legacy flat keys stay for webhook consumers");
        p.GetProperty("paymentMethod").GetString().Should().Be("Cash");
    }

    [Fact]
    public async Task ComandaClose_OrderCompleted_Payload_HasNestedOrder_AndTerminal()
    {
        var product = new Product { Sku = "P1", Name = "Pan", Price = 1000m };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        var shift = await _s.OpenShiftAsync("Luis", 0m);

        var comandas = new ComandaService(_db, new ComandaSettings { ConfirmationDelaySeconds = 30 }, _clock, null, _sat);
        var comanda = await comandas.OpenComandaAsync("Mesa 3", "Mesero");
        await comandas.AddItemAsync(comanda.Id, product.Id, 2);
        var closed = await comandas.CloseAndPayAsync(comanda.Id, shift.Id, PaymentMethod.Cash, 2000m);
        closed.Success.Should().BeTrue(closed.ErrorMessage);

        var p = await LastPayloadAsync(SyncEventTypes.OrderCompleted, closed.OrderId);
        var o = p.GetProperty("order");
        o.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        o.GetProperty("items").GetArrayLength().Should().Be(1);
        o.GetProperty("payments").GetArrayLength().Should().Be(1);
        o.GetProperty("payments")[0].GetProperty("method").GetString().Should().Be("Cash");
        (await _db.Orders.AsNoTracking().SingleAsync(x => x.Id == closed.OrderId)).TerminalCode.Should().Be("CAJA-02");
    }

    [Fact]
    public async Task BillPayment_Payloads_HaveNestedBillPayment()
    {
        var shift = await _s.OpenShiftAsync("Luis", 100000m);
        var biller = await AddBillerAsync(_db, "Energia", fixedCommission: 1500m);
        var bills = new BillPaymentService(_db, _clock, _sat);
        var userId = Guid.NewGuid();

        var dto = await bills.RecordAsync(new BillPaymentRequest(biller.Id, "CTA-77", 20000m, PaymentMethod.Cash, 25000m, userId, "Luis", "Doña Rosa"));

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventType == SyncEventTypes.BillPaymentRecorded);
        msg.AggregateType.Should().Be("BillPayment");
        msg.AggregateId.Should().Be(dto.Id);
        var p = await LastPayloadAsync(SyncEventTypes.BillPaymentRecorded, dto.Id);
        var b = p.GetProperty("billPayment");
        b.GetProperty("id").GetGuid().Should().Be(dto.Id);
        b.GetProperty("cashShiftId").GetGuid().Should().Be(shift.Id);
        b.GetProperty("terminalId").GetGuid().Should().Be(_sat.Id);
        b.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        b.GetProperty("billerId").GetGuid().Should().Be(biller.Id);
        b.GetProperty("billerName").GetString().Should().Be("Energia");
        b.GetProperty("accountReference").GetString().Should().Be("CTA-77");
        b.GetProperty("customerName").GetString().Should().Be("Doña Rosa");
        b.GetProperty("amount").GetDecimal().Should().Be(20000m);
        b.GetProperty("commission").GetDecimal().Should().Be(1500m);
        b.GetProperty("method").GetString().Should().Be("Cash");
        b.GetProperty("tenderedAmount").GetDecimal().Should().Be(25000m);
        b.GetProperty("changeAmount").GetDecimal().Should().Be(3500m);
        b.GetProperty("receiptNumber").GetString().Should().Be("REC-000001");
        b.GetProperty("status").GetString().Should().Be("Completed");
        b.GetProperty("userId").GetGuid().Should().Be(userId);
        b.GetProperty("userName").GetString().Should().Be("Luis");
        b.GetProperty("paidAt").GetDateTime().Should().Be(_clock.GetUtcNow().UtcDateTime);
        b.GetProperty("voidedAt").ValueKind.Should().Be(JsonValueKind.Null);
        b.GetProperty("voidOverrideId").ValueKind.Should().Be(JsonValueKind.Null);
        b.GetProperty("voidReason").ValueKind.Should().Be(JsonValueKind.Null);
        p.GetProperty("billPaymentId").GetGuid().Should().Be(dto.Id, "legacy flat keys");

        _clock.Advance(TimeSpan.FromMinutes(2));
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.VoidBillPayment, "Gerente");
        await bills.VoidAsync(dto.Id, "Error de referencia", oid);

        var v = (await LastPayloadAsync(SyncEventTypes.BillPaymentVoided, dto.Id));
        var vb = v.GetProperty("billPayment");
        vb.GetProperty("id").GetGuid().Should().Be(dto.Id);
        vb.GetProperty("status").GetString().Should().Be("Voided");
        vb.GetProperty("voidedAt").GetDateTime().Should().Be(_clock.GetUtcNow().UtcDateTime);
        vb.GetProperty("voidOverrideId").GetGuid().Should().Be(oid);
        vb.GetProperty("voidReason").GetString().Should().Be("Error de referencia");
        vb.GetProperty("receiptNumber").GetString().Should().Be("REC-000001");
        v.GetProperty("managerName").GetString().Should().Be("Gerente", "writer-specific extra keys are merged at top level");
    }
}
