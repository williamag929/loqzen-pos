using Core.Application.DTOs;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using static Loqzen.Tests.Fixtures.CashOpsData;

namespace Loqzen.Tests.Services;

/// <summary>Full cuadre: sales by method, recaudos by method, movements by type, expected cash/card/transfer.</summary>
public class ShiftReconciliationTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly CashShiftService _shifts;
    private readonly BillPaymentService _bills;

    public ShiftReconciliationTests()
    {
        _db = TestDbContextFactory.Create();
        // No PIN for movements here: this class is about the numbers.
        _shifts = new CashShiftService(_db, _clock, new CashShiftSettings { ManagerRequiredFor = new List<string>() });
        _bills = new BillPaymentService(_db, _clock);
    }

    public void Dispose() => _db.Dispose();

    private BillPaymentRequest Bill(Guid billerId, decimal amount, PaymentMethod method, decimal tendered = 0m) =>
        new(billerId, "REF-" + amount, amount, method, tendered, null, "Cajero");

    /// <summary>
    /// Opening 100.000; sales cash 50.000 (2 orders), card 45.000, transfer 15.000 (+ cancelled/refunded excluded);
    /// recaudos cash 51.500, card 10.300, transfer 21.500 (+ voided cash 41.500); movements +10.000 −5.000 −3.000 −50.000 −2.000.
    /// </summary>
    private async Task<Guid> BuildDayAsync()
    {
        var shift = await _shifts.OpenShiftAsync("Ana", 100000m);
        AddSale(_db, shift.Id, PaymentMethod.Cash, 30000m);
        AddSale(_db, shift.Id, PaymentMethod.Cash, 20000m);
        AddSale(_db, shift.Id, PaymentMethod.Card, 45000m);
        AddSale(_db, shift.Id, PaymentMethod.Transfer, 15000m);
        AddSale(_db, shift.Id, PaymentMethod.Cash, 99000m, OrderStatus.Cancelled);
        AddSale(_db, shift.Id, PaymentMethod.Card, 7000m, OrderStatus.Refunded);
        AddSale(_db, shift.Id, PaymentMethod.Cash, 11000m, OrderStatus.Pending);

        var energia = await AddBillerAsync(_db, "Energia", fixedCommission: 1500m);
        var recargas = await AddBillerAsync(_db, "Recargas", percent: 0.03m);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _bills.RecordAsync(Bill(energia.Id, 50000m, PaymentMethod.Cash, 60000m));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _bills.RecordAsync(Bill(recargas.Id, 10000m, PaymentMethod.Card));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _bills.RecordAsync(Bill(energia.Id, 20000m, PaymentMethod.Transfer));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var voided = await _bills.RecordAsync(Bill(energia.Id, 40000m, PaymentMethod.Cash, 41500m));
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.VoidBillPayment);
        await _bills.VoidAsync(voided.Id, "Cliente se arrepintió", oid);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _shifts.RecordMovementAsync(Movement(CashMovementType.PaidIn, 10000m, "Sencillo"));
        await _shifts.RecordMovementAsync(Movement(CashMovementType.PaidOut, 5000m, "Proveedor hielo"));
        await _shifts.RecordMovementAsync(Movement(CashMovementType.Expense, 3000m, "Bolsas"));
        await _shifts.RecordMovementAsync(Movement(CashMovementType.SafeDrop, 50000m, "Sobre 1"));
        await _shifts.RecordMovementAsync(Movement(CashMovementType.TipPayout, 2000m, "Propinas"));
        return shift.Id;
    }

    [Fact]
    public async Task OpenShift_ReconciliationFields_AreExact()
    {
        var shiftId = await BuildDayAsync();

        var r = await _shifts.GetReconciliationAsync();

        r.ShiftId.Should().Be(shiftId);
        r.ShiftNumber.Should().Be(1);
        r.CashierName.Should().Be("Ana");
        r.Status.Should().Be(CashShiftStatus.Open);
        r.OpeningAmount.Should().Be(100000m);

        r.CashSales.Should().Be(50000m, "cancelled/refunded/pending orders are excluded");
        r.CardSales.Should().Be(45000m);
        r.TransferSales.Should().Be(15000m);
        r.OtherSales.Should().Be(0m);
        r.TotalSales.Should().Be(110000m);
        r.OrderCount.Should().Be(4);

        r.BillPaymentCount.Should().Be(3, "the voided recaudo is excluded");
        r.BillPaymentsCash.Should().Be(51500m);
        r.BillPaymentsCard.Should().Be(10300m);
        r.BillPaymentsTransfer.Should().Be(21500m);
        r.BillPaymentCommissions.Should().Be(3300m);
        r.BillPaymentsToRemit.Should().Be(80000m);

        r.PaidIn.Should().Be(10000m);
        r.PaidOut.Should().Be(5000m);
        r.Expenses.Should().Be(3000m);
        r.SafeDrops.Should().Be(50000m);
        r.TipPayouts.Should().Be(2000m);
        r.BillPaymentVoidsCash.Should().Be(0m, "a voided recaudo simply drops out; the void movement is display-only");

        r.ExpectedCash.Should().Be(151500m); // 100000 + 50000 + 51500 + 10000 - 5000 - 3000 - 50000 - 2000
        r.ExpectedCard.Should().Be(55300m);
        r.ExpectedTransfer.Should().Be(36500m);

        r.Movements.Should().HaveCount(8, "5 manual + 2 recaudo cash + 1 void");
        r.Movements.Count(m => m.Type == CashMovementType.BillPayment).Should().Be(2);
        r.Movements.Should().ContainSingle(m => m.Type == CashMovementType.BillPaymentVoid).Which.Amount.Should().Be(41500m);
        r.Movements.Select(m => m.OccurredAt).Should().BeInAscendingOrder();

        r.CountedCash.Should().BeNull();
        r.Difference.Should().BeNull();
        r.CountLines.Should().BeEmpty();
    }

    [Fact]
    public async Task VoidedCashRecaudo_DropsOutOfExpectedCash()
    {
        var shift = await _shifts.OpenShiftAsync("Ana", 0m);
        var biller = await AddBillerAsync(_db, "Gas", fixedCommission: 1000m);
        var p = await _bills.RecordAsync(Bill(biller.Id, 30000m, PaymentMethod.Cash, 31000m));
        (await _shifts.GetReconciliationAsync()).ExpectedCash.Should().Be(31000m);

        await _bills.VoidAsync(p.Id, "Error", await AddOverrideAsync(_db, _clock, AuthorizedAction.VoidBillPayment));

        var r = await _shifts.GetReconciliationAsync(shift.Id);
        r.ExpectedCash.Should().Be(0m);
        r.BillPaymentCount.Should().Be(0);
        r.BillPaymentCommissions.Should().Be(0m);
    }

    [Fact]
    public async Task ClosedShift_ByShiftId_IncludesCountAndApprovalData()
    {
        var shiftId = await BuildDayAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _shifts.RecordPartialCountAsync(Count(150000m), "Supervisor");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.CashDifference, "Gerente Luis");

        var lines = new[]
        {
            new CashCountLineDto { Denomination = 50000m, Quantity = 2 },
            new CashCountLineDto { Denomination = 1000m, Quantity = 40 },
            new CashCountLineDto { Denomination = 100000m, Quantity = 0 },
        };
        await _shifts.CloseShiftAsync(new CloseShiftRequest(lines, 40000m, "Faltante revisado", Guid.NewGuid(), "Ana", true, oid));

        var r = await _shifts.GetReconciliationAsync(shiftId);

        r.Status.Should().Be(CashShiftStatus.Closed);
        r.ClosedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        r.ClosedByName.Should().Be("Ana");
        r.ExpectedCash.Should().Be(151500m);
        r.CountedCash.Should().Be(140000m);
        r.Difference.Should().Be(-11500m);
        r.FloatLeftAmount.Should().Be(40000m);
        r.WasBlindCount.Should().BeTrue();
        r.DifferenceApprovedBy.Should().Be("Gerente Luis");
        r.Notes.Should().Be("Faltante revisado");
        r.CountLines.Select(l => (l.Denomination, l.Quantity)).Should().Equal((50000m, 2), (1000m, 40));
        r.PartialCounts.Should().ContainSingle();
        r.PartialCounts[0].ExpectedAmount.Should().Be(151500m);
        r.PartialCounts[0].CountedAmount.Should().Be(150000m);
        r.PartialCounts[0].Difference.Should().Be(-1500m);
        r.PartialCounts[0].UserName.Should().Be("Supervisor");
    }

    [Fact]
    public async Task UnknownShiftId_Throws()
    {
        var act = () => _shifts.GetReconciliationAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no encontrado*");
    }

    [Fact]
    public async Task NoOpenShift_Throws()
    {
        var act = () => _shifts.GetReconciliationAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turno abierto*");
    }

    [Fact]
    public async Task OtherShiftsData_IsNotMixedIn()
    {
        var first = await _shifts.OpenShiftAsync("Ana", 0m);
        AddSale(_db, first.Id, PaymentMethod.Cash, 10000m);
        await _shifts.CloseShiftAsync(Close(10000m));

        var second = await _shifts.OpenShiftAsync("Beto", 5000m);
        AddSale(_db, second.Id, PaymentMethod.Cash, 2000m);

        var r = await _shifts.GetReconciliationAsync();
        r.ShiftNumber.Should().Be(2);
        r.CashSales.Should().Be(2000m);
        r.ExpectedCash.Should().Be(7000m);
    }
}
