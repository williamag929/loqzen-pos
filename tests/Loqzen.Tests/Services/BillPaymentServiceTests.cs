using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static Loqzen.Tests.Fixtures.CashOpsData;

namespace Loqzen.Tests.Services;

/// <summary>Recaudos (bill payments): receipt numbers, commission, cash/card, void with manager PIN, billers CRUD.</summary>
public class BillPaymentServiceTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly BillPaymentService _service;
    private readonly CashShiftService _shifts;

    public BillPaymentServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new BillPaymentService(_db, _clock);
        _shifts = new CashShiftService(_db, _clock);
    }

    public void Dispose() => _db.Dispose();

    private static BillPaymentRequest Req(Guid billerId, decimal amount, PaymentMethod method = PaymentMethod.Cash,
        decimal tendered = 0m, string reference = "123456", string? customer = null) =>
        new(billerId, reference, amount, method, tendered, null, "Cajero", customer);

    private async Task<Biller> OpenWithBillerAsync(decimal fixedCommission = 1500m, decimal percent = 0m)
    {
        await _shifts.OpenShiftAsync("Ana", 100000m);
        return await AddBillerAsync(_db, "Energia", fixedCommission, percent);
    }

    private Task<Guid> VoidOverrideAsync(AuthorizedAction action = AuthorizedAction.VoidBillPayment) =>
        AddOverrideAsync(_db, _clock, action);

    // ── Record ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiptNumbers_AreSequential()
    {
        var b = await OpenWithBillerAsync();
        var p1 = await _service.RecordAsync(Req(b.Id, 10000m, PaymentMethod.Card));
        var p2 = await _service.RecordAsync(Req(b.Id, 10000m, PaymentMethod.Card));
        p1.ReceiptNumber.Should().Be("REC-000001");
        p2.ReceiptNumber.Should().Be("REC-000002");
    }

    [Fact]
    public async Task ReceiptNumbers_ContinueAfterExisting()
    {
        var b = await OpenWithBillerAsync();
        var shift = (await _shifts.GetCurrentShiftAsync())!;
        _db.BillPayments.Add(new BillPayment { CashShiftId = shift.Id, BillerId = b.Id, BillerName = b.Name, AccountReference = "x", Amount = 1, ReceiptNumber = "REC-000041" });
        _db.BillPayments.Add(new BillPayment { CashShiftId = shift.Id, BillerId = b.Id, BillerName = b.Name, AccountReference = "y", Amount = 1, ReceiptNumber = "REC-000009" });
        await _db.SaveChangesAsync();

        var p = await _service.RecordAsync(Req(b.Id, 10000m, PaymentMethod.Card));

        p.ReceiptNumber.Should().Be("REC-000042");
    }

    [Theory]
    [InlineData(1500, 0, 50000, 1500)]
    [InlineData(0, 0.03, 33333, 1000)]   // 999.99 -> 1000
    [InlineData(0, 0.03, 10000, 300)]
    [InlineData(1000, 0.01, 15050, 1151)] // 1000 + 150.5 -> 1151 (away from zero)
    [InlineData(0, 0, 20000, 0)]
    public async Task Commission_FixedPlusPercent_RoundedToPesos(decimal fixedC, decimal pct, decimal amount, decimal expected)
    {
        var b = await OpenWithBillerAsync(fixedC, pct);
        var p = await _service.RecordAsync(Req(b.Id, amount, PaymentMethod.Card));
        p.Commission.Should().Be(expected);
        p.Total.Should().Be(amount + expected);
    }

    [Fact]
    public async Task Cash_TenderedBelowTotal_Throws_NothingSaved()
    {
        var b = await OpenWithBillerAsync();
        var act = () => _service.RecordAsync(Req(b.Id, 50000m, PaymentMethod.Cash, tendered: 51499m));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*menor que el total*");
        (await _db.BillPayments.CountAsync()).Should().Be(0);
        (await _db.CashMovements.CountAsync()).Should().Be(0);
        (await _db.OutboxMessages.CountAsync(o => o.EventType != "CashShiftOpened")).Should().Be(0);
    }

    [Fact]
    public async Task Cash_ComputesChange_CreatesBillPaymentMovement_AndOutbox()
    {
        var b = await OpenWithBillerAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));

        var p = await _service.RecordAsync(Req(b.Id, 50000m, PaymentMethod.Cash, tendered: 60000m, reference: " 998877 ", customer: " Juan "));

        p.Amount.Should().Be(50000m);
        p.Commission.Should().Be(1500m);
        p.Total.Should().Be(51500m);
        p.TenderedAmount.Should().Be(60000m);
        p.ChangeAmount.Should().Be(8500m);
        p.AccountReference.Should().Be("998877");
        p.CustomerName.Should().Be("Juan");
        p.BillerName.Should().Be("Energia");
        p.Status.Should().Be(BillPaymentStatus.Completed);
        p.PaidAt.Should().Be(_clock.GetUtcNow().UtcDateTime);

        var m = await _db.CashMovements.AsNoTracking().SingleAsync();
        m.Type.Should().Be(CashMovementType.BillPayment);
        m.Amount.Should().Be(51500m);
        m.Reference.Should().Be(p.ReceiptNumber);
        m.BillPaymentId.Should().Be(p.Id);

        var o = await _db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EventType != "CashShiftOpened");
        o.EventType.Should().Be("BillPaymentRecorded");
        o.AggregateId.Should().Be(p.Id);
        o.PayloadJson.Should().Contain("REC-000001");

        (await _shifts.GetReconciliationAsync()).ExpectedCash.Should().Be(151500m, "movement is display-only: no double count");
    }

    [Theory]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.Transfer)]
    public async Task NonCash_TenderedEqualsTotal_NoCashMovement(PaymentMethod method)
    {
        var b = await OpenWithBillerAsync();
        var p = await _service.RecordAsync(Req(b.Id, 50000m, method, tendered: 999999m));
        p.TenderedAmount.Should().Be(51500m);
        p.ChangeAmount.Should().Be(0m);
        (await _db.CashMovements.CountAsync()).Should().Be(0);
        (await _shifts.GetReconciliationAsync()).ExpectedCash.Should().Be(100000m);
    }

    [Fact]
    public async Task RequiresOpenShift()
    {
        var b = await AddBillerAsync(_db, "Energia", 1500m);
        var act = () => _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turno abierto*");
    }

    [Fact]
    public async Task InactiveOrUnknownBiller_Rejected()
    {
        await _shifts.OpenShiftAsync("Ana", 0m);
        var inactive = await AddBillerAsync(_db, "Viejo", active: false);

        var a1 = () => _service.RecordAsync(Req(inactive.Id, 1000m, PaymentMethod.Card));
        await a1.Should().ThrowAsync<InvalidOperationException>().WithMessage("Convenio no encontrado o inactivo.");
        var a2 = () => _service.RecordAsync(Req(Guid.NewGuid(), 1000m, PaymentMethod.Card));
        await a2.Should().ThrowAsync<InvalidOperationException>().WithMessage("Convenio no encontrado o inactivo.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankReference_Rejected(string reference)
    {
        var b = await OpenWithBillerAsync();
        var act = () => _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card, reference: reference));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*referencia*");
    }

    [Fact]
    public async Task ReferenceLongerThan60_Rejected()
    {
        var b = await OpenWithBillerAsync();
        var act = () => _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card, reference: new string('9', 61)));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*60*");
        (await _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card, reference: new string('9', 60)))).AccountReference.Should().HaveLength(60);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveAmount_Rejected(int amount)
    {
        var b = await OpenWithBillerAsync();
        var act = () => _service.RecordAsync(Req(b.Id, amount, PaymentMethod.Card));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mayor que cero*");
    }

    [Fact]
    public async Task OtherMethod_Rejected()
    {
        var b = await OpenWithBillerAsync();
        var act = () => _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Other, 5000m));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Medio de pago no permitido para recaudos.*");
    }

    // ── Void ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Cash_ConsumesOverride_AddsVoidMovement_RemovesFromExpected()
    {
        var b = await OpenWithBillerAsync();
        var p = await _service.RecordAsync(Req(b.Id, 50000m, PaymentMethod.Cash, 51500m));
        (await _shifts.GetReconciliationAsync()).ExpectedCash.Should().Be(151500m);
        var oid = await VoidOverrideAsync();
        _clock.Advance(TimeSpan.FromMinutes(4));

        var v = await _service.VoidAsync(p.Id, "  Referencia equivocada ", oid);

        v.Status.Should().Be(BillPaymentStatus.Voided);
        v.VoidedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        v.VoidReason.Should().Be("Referencia equivocada");
        var stored = await _db.BillPayments.AsNoTracking().SingleAsync();
        stored.VoidOverrideId.Should().Be(oid);
        var o = await _db.ManagerOverrides.AsNoTracking().SingleAsync();
        o.ConsumedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        o.ConsumedFor.Should().Be($"BillPayment {p.Id}");

        var voidMove = await _db.CashMovements.AsNoTracking().SingleAsync(m => m.Type == CashMovementType.BillPaymentVoid);
        voidMove.Amount.Should().Be(51500m);
        voidMove.Reference.Should().Be(p.ReceiptNumber);
        voidMove.ManagerOverrideId.Should().Be(oid);
        voidMove.BillPaymentId.Should().Be(p.Id);
        (await _db.OutboxMessages.AsNoTracking().CountAsync(m => m.EventType == "BillPaymentVoided" && m.AggregateId == p.Id)).Should().Be(1);

        (await _shifts.GetReconciliationAsync()).ExpectedCash.Should().Be(100000m);
    }

    [Fact]
    public async Task Void_Card_NoCashMovement()
    {
        var b = await OpenWithBillerAsync();
        var p = await _service.RecordAsync(Req(b.Id, 50000m, PaymentMethod.Card));
        await _service.VoidAsync(p.Id, "Error", await VoidOverrideAsync());
        (await _db.CashMovements.CountAsync()).Should().Be(0);
        (await _shifts.GetReconciliationAsync()).ExpectedCard.Should().Be(0m);
    }

    [Fact]
    public async Task Void_WithoutValidOverride_Rejected_AndNothingChanges()
    {
        var b = await OpenWithBillerAsync();
        var p = await _service.RecordAsync(Req(b.Id, 50000m, PaymentMethod.Cash, 51500m));
        var wrong = await VoidOverrideAsync(AuthorizedAction.CashMovement);

        var a1 = () => _service.VoidAsync(p.Id, "Error", wrong);
        await a1.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        var a2 = () => _service.VoidAsync(p.Id, "Error", Guid.NewGuid());
        await a2.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");

        (await _db.BillPayments.AsNoTracking().SingleAsync()).Status.Should().Be(BillPaymentStatus.Completed);
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync()).ConsumedAt.Should().BeNull();
        (await _db.CashMovements.CountAsync(m => m.Type == CashMovementType.BillPaymentVoid)).Should().Be(0);
    }

    [Fact]
    public async Task Void_ReusingConsumedOverride_Rejected()
    {
        var b = await OpenWithBillerAsync();
        var p1 = await _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card));
        var p2 = await _service.RecordAsync(Req(b.Id, 2000m, PaymentMethod.Card));
        var oid = await VoidOverrideAsync();
        await _service.VoidAsync(p1.Id, "Error", oid);

        var act = () => _service.VoidAsync(p2.Id, "Error", oid);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Void_ReasonRequired(string reason)
    {
        var b = await OpenWithBillerAsync();
        var p = await _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card));
        var oid = await VoidOverrideAsync();
        var act = () => _service.VoidAsync(p.Id, reason, oid);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*motivo*");
    }

    [Fact]
    public async Task Void_AlreadyVoided_Rejected_SecondOverrideNotConsumed()
    {
        var b = await OpenWithBillerAsync();
        var p = await _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card));
        await _service.VoidAsync(p.Id, "Error", await VoidOverrideAsync());
        var second = await VoidOverrideAsync();

        var act = () => _service.VoidAsync(p.Id, "Otra vez", second);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("El recaudo ya está anulado.");
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync(o => o.Id == second)).ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task Void_FromClosedShift_Rejected()
    {
        var b = await OpenWithBillerAsync();
        var p = await _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card));
        await _shifts.CloseShiftAsync(Close(100000m));
        await _shifts.OpenShiftAsync("Beto", 0m);

        var oid = await VoidOverrideAsync();
        var act = () => _service.VoidAsync(p.Id, "Tarde", oid);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Solo se pueden anular recaudos del turno abierto.");
    }

    [Fact]
    public async Task Void_Unknown_Rejected()
    {
        await OpenWithBillerAsync();
        var act = () => _service.VoidAsync(Guid.NewGuid(), "x", Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no encontrado*");
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetForShift_NewestFirst_IncludingVoided_AndOnlyThatShift()
    {
        var b = await OpenWithBillerAsync();
        var first = await _service.RecordAsync(Req(b.Id, 1000m, PaymentMethod.Card));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await _service.RecordAsync(Req(b.Id, 2000m, PaymentMethod.Card));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var third = await _service.RecordAsync(Req(b.Id, 3000m, PaymentMethod.Card));
        await _service.VoidAsync(second.Id, "Error", await VoidOverrideAsync());
        var shiftId = (await _shifts.GetCurrentShiftAsync())!.Id;

        var list = await _service.GetForShiftAsync();

        list.Select(p => p.Id).Should().Equal(third.Id, second.Id, first.Id);
        list[1].Status.Should().Be(BillPaymentStatus.Voided);

        await _shifts.CloseShiftAsync(Close(100000m));
        (await _service.GetForShiftAsync()).Should().BeEmpty("no open shift");
        (await _service.GetForShiftAsync(shiftId)).Should().HaveCount(3);
        await _shifts.OpenShiftAsync("Beto", 0m);
        (await _service.GetForShiftAsync()).Should().BeEmpty("new shift has no recaudos");
    }

    // ── Billers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveBiller_CreatesAndUpdates()
    {
        var created = await _service.SaveBillerAsync(new BillerDto { Name = "  Agua  ", Code = " AGUA ", CommissionFixed = 1000m, CommissionPercent = 0.01m, ColorHex = "#00FF00", SortOrder = 3, IsActive = true });

        created.Id.Should().NotBeEmpty();
        created.Name.Should().Be("Agua");
        created.Code.Should().Be("AGUA");

        created.Name = "Acueducto";
        created.CommissionFixed = 2000m;
        var updated = await _service.SaveBillerAsync(created);

        updated.Id.Should().Be(created.Id);
        var stored = await _db.Billers.AsNoTracking().SingleAsync();
        stored.Name.Should().Be("Acueducto");
        stored.CommissionFixed.Should().Be(2000m);
        stored.CommissionPercent.Should().Be(0.01m);
        stored.SortOrder.Should().Be(3);
    }

    [Fact]
    public async Task SaveBiller_KeepingOwnName_IsNotADuplicate()
    {
        var b = await _service.SaveBillerAsync(new BillerDto { Name = "Gas", IsActive = true });
        b.CommissionFixed = 500m;
        (await _service.SaveBillerAsync(b)).CommissionFixed.Should().Be(500m);
    }

    [Fact]
    public async Task SaveBiller_DuplicateName_CaseInsensitive_Rejected()
    {
        await _service.SaveBillerAsync(new BillerDto { Name = "Energia", IsActive = true });
        var act = () => _service.SaveBillerAsync(new BillerDto { Name = "ENERGIA", IsActive = true });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ya existe un convenio llamado 'ENERGIA'.");
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -0.01)]
    [InlineData(0, 0.21)]
    public async Task SaveBiller_InvalidCommission_Rejected(decimal fixedC, decimal pct)
    {
        var act = () => _service.SaveBillerAsync(new BillerDto { Name = "X", CommissionFixed = fixedC, CommissionPercent = pct });
        await act.Should().ThrowAsync<ArgumentException>();
        (await _db.Billers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SaveBiller_PercentAtMax_Accepted_AndBlankOrLongName_Rejected()
    {
        (await _service.SaveBillerAsync(new BillerDto { Name = "Max", CommissionPercent = 0.2m })).CommissionPercent.Should().Be(0.2m);
        var blank = () => _service.SaveBillerAsync(new BillerDto { Name = "  " });
        await blank.Should().ThrowAsync<ArgumentException>().WithMessage("*nombre*");
        var longName = () => _service.SaveBillerAsync(new BillerDto { Name = new string('a', 101) });
        await longName.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetBillers_ExcludesInactiveByDefault_OrderedBySortThenName()
    {
        _db.Billers.AddRange(
            new Biller { Name = "Zeta", SortOrder = 1 },
            new Biller { Name = "Alfa", SortOrder = 2 },
            new Biller { Name = "Beta", SortOrder = 1 },
            new Biller { Name = "Inactivo", SortOrder = 0, IsActive = false });
        await _db.SaveChangesAsync();

        (await _service.GetBillersAsync()).Select(b => b.Name).Should().Equal("Beta", "Zeta", "Alfa");
        (await _service.GetBillersAsync(includeInactive: true)).Select(b => b.Name).Should().Equal("Inactivo", "Beta", "Zeta", "Alfa");
    }
}
