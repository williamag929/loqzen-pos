using Core.Application.DTOs;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static Loqzen.Tests.Fixtures.CashOpsData;

namespace Loqzen.Tests.Integration;

/// <summary>
/// Cuadre de caja + recaudos against REAL SQLite (migrated by DbInitializer): proves every query of
/// CashShiftService / BillPaymentService translates to SQL (decimal sums in memory, enum filters, ordering).
/// </summary>
public class CashOperationsSqliteTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TestClock _clock = new();

    public CashOperationsSqliteTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    private PosDbContext NewContext() => new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_conn).Options);

    [Fact]
    public async Task SeededBillers_ExistAfterInitialize()
    {
        await using var db = NewContext();
        await DbInitializer.InitializeAsync(db);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        var billers = await new BillPaymentService(db, _clock).GetBillersAsync();
        billers.Should().HaveCount(5);
        billers.Select(b => b.SortOrder).Should().BeInAscendingOrder();
        billers.Should().Contain(b => b.Code == "RECARGAS" && b.CommissionPercent == 0.03m);
    }

    [Fact]
    public async Task FullDay_Open_Sell_Recaudos_Movements_PartialCount_CloseWithApproval_OnSqlite()
    {
        Guid shiftId, voidedId;
        await using (var db = NewContext())
        {
            await DbInitializer.InitializeAsync(db);
            var shifts = new CashShiftService(db, _clock);
            var bills = new BillPaymentService(db, _clock);

            (await shifts.GetSuggestedOpeningAmountAsync()).Should().Be(200000m, "default float, no shift yet");
            var shift = await shifts.OpenShiftAsync("Cajero", 200000m, Guid.NewGuid());
            shiftId = shift.Id;

            // Sales (inserted directly, as the checkout would).
            AddSale(db, shiftId, PaymentMethod.Cash, 45000m);
            AddSale(db, shiftId, PaymentMethod.Cash, 15500m);
            AddSale(db, shiftId, PaymentMethod.Card, 72000m);
            AddSale(db, shiftId, PaymentMethod.Transfer, 30000m);
            AddSale(db, shiftId, PaymentMethod.Cash, 99999m, OrderStatus.Cancelled);

            var billers = await bills.GetBillersAsync();
            var energia = billers.Single(b => b.Code == "ENERGIA");     // 1.500 fixed
            var recargas = billers.Single(b => b.Code == "RECARGAS");   // 3 %

            _clock.Advance(TimeSpan.FromMinutes(5));
            var r1 = await bills.RecordAsync(new BillPaymentRequest(energia.Id, "5544332211", 87350m, PaymentMethod.Cash, 100000m, null, "Cajero", "Pedro"));
            r1.ReceiptNumber.Should().Be("REC-000001");
            r1.ChangeAmount.Should().Be(11150m);
            _clock.Advance(TimeSpan.FromMinutes(5));
            var r2 = await bills.RecordAsync(new BillPaymentRequest(recargas.Id, "3001234567", 20000m, PaymentMethod.Card, 0m, null, "Cajero"));
            r2.ReceiptNumber.Should().Be("REC-000002");
            r2.Commission.Should().Be(600m);
            _clock.Advance(TimeSpan.FromMinutes(5));
            var r3 = await bills.RecordAsync(new BillPaymentRequest(energia.Id, "999", 10000m, PaymentMethod.Cash, 11500m, null, "Cajero"));
            voidedId = r3.Id;
            await bills.VoidAsync(r3.Id, "Referencia errada", await AddOverrideAsync(db, _clock, AuthorizedAction.VoidBillPayment));

            _clock.Advance(TimeSpan.FromMinutes(5));
            await shifts.RecordMovementAsync(Movement(CashMovementType.PaidIn, 20000m, "Sencillo"));
            await shifts.RecordMovementAsync(Movement(CashMovementType.Expense, 8500m, "Bolsas",
                await AddOverrideAsync(db, _clock, AuthorizedAction.CashMovement)));
            await shifts.RecordMovementAsync(Movement(CashMovementType.SafeDrop, 150000m, "Sobre 1",
                await AddOverrideAsync(db, _clock, AuthorizedAction.CashMovement)));

            // expected = 200000 + 60500 + 88850 + 20000 - 8500 - 150000 = 210850
            _clock.Advance(TimeSpan.FromMinutes(5));
            var partial = await shifts.RecordPartialCountAsync(Count(210000m), "Supervisor");
            partial.ExpectedAmount.Should().Be(210850m);

            _clock.Advance(TimeSpan.FromHours(1));
            var lines = new[]
            {
                new CashCountLineDto { Denomination = 100000m, Quantity = 1 },
                new CashCountLineDto { Denomination = 50000m, Quantity = 2 },
                new CashCountLineDto { Denomination = 1000m, Quantity = 0 },
            }; // 200000 → difference -10850 > 5000
            var noApproval = () => shifts.CloseShiftAsync(new CloseShiftRequest(lines, 100000m, "Faltante", null, "Cajero", true));
            await noApproval.Should().ThrowAsync<CashDifferenceRequiresApprovalException>();

            var oid = await AddOverrideAsync(db, _clock, AuthorizedAction.CashDifference, "Administrador");
            var closed = await shifts.CloseShiftAsync(new CloseShiftRequest(lines, 100000m, "Faltante revisado", null, "Cajero", true, oid));
            closed.Difference.Should().Be(-10850m);
        }

        // Fresh context: everything read back through SQL.
        await using (var check = NewContext())
        {
            var shifts = new CashShiftService(check, _clock);
            var r = await shifts.GetReconciliationAsync(shiftId);

            r.Status.Should().Be(CashShiftStatus.Closed);
            r.OpeningAmount.Should().Be(200000m);
            r.CashSales.Should().Be(60500m);
            r.CardSales.Should().Be(72000m);
            r.TransferSales.Should().Be(30000m);
            r.OrderCount.Should().Be(4);
            r.BillPaymentCount.Should().Be(2);
            r.BillPaymentsCash.Should().Be(88850m);
            r.BillPaymentsCard.Should().Be(20600m);
            r.BillPaymentsTransfer.Should().Be(0m);
            r.BillPaymentCommissions.Should().Be(2100m);
            r.BillPaymentsToRemit.Should().Be(107350m);
            r.PaidIn.Should().Be(20000m);
            r.Expenses.Should().Be(8500m);
            r.SafeDrops.Should().Be(150000m);
            r.ExpectedCash.Should().Be(210850m);
            r.ExpectedCard.Should().Be(92600m);
            r.ExpectedTransfer.Should().Be(30000m);
            r.CountedCash.Should().Be(200000m);
            r.Difference.Should().Be(-10850m);
            r.FloatLeftAmount.Should().Be(100000m);
            r.WasBlindCount.Should().BeTrue();
            r.DifferenceApprovedBy.Should().Be("Administrador");
            r.CountLines.Select(l => l.Denomination).Should().Equal(100000m, 50000m);
            r.PartialCounts.Should().ContainSingle().Which.CountedAmount.Should().Be(210000m);
            r.Movements.Should().HaveCount(6, "3 manual + 2 recaudo cash + 1 void");
            r.Movements.Select(m => m.OccurredAt).Should().BeInAscendingOrder();

            (await shifts.GetSuggestedOpeningAmountAsync()).Should().Be(100000m);
            var history = await shifts.GetHistoryAsync();
            history.Should().ContainSingle().Which.HadManagerApproval.Should().BeTrue();

            var bills = new BillPaymentService(check, _clock);
            var list = await bills.GetForShiftAsync(shiftId);
            list.Select(b => b.ReceiptNumber).Should().Equal("REC-000003", "REC-000002", "REC-000001");
            list.Single(b => b.Id == voidedId).Status.Should().Be(BillPaymentStatus.Voided);

            (await check.ManagerOverrides.CountAsync(o => o.ConsumedAt == null)).Should().Be(0, "every override was consumed once");
            var events = await check.OutboxMessages.Select(o => o.EventType).ToListAsync();
            events.Should().Contain(new[] { "BillPaymentRecorded", "BillPaymentVoided", "CashMovementRecorded", "CashShiftClosed" });
            events.Count(e => e == "CashMovementRecorded").Should().Be(3);
            events.Count(e => e == "BillPaymentRecorded").Should().Be(3);

            // Next shift continues numbering and uses the float as suggestion.
            var shifts2 = new CashShiftService(check, _clock);
            await shifts2.OpenShiftAsync("Cajero 2", 100000m);
            var b = (await bills.GetBillersAsync()).First();
            (await bills.RecordAsync(new BillPaymentRequest(b.Id, "1", 1000m, PaymentMethod.Transfer, 0m, null, "Cajero 2")))
                .ReceiptNumber.Should().Be("REC-000004");
        }
    }

    [Fact]
    public async Task BillerCrud_DuplicateCheck_TranslatesToSql()
    {
        await using var db = NewContext();
        await DbInitializer.InitializeAsync(db);
        var bills = new BillPaymentService(db, _clock);

        var dup = () => bills.SaveBillerAsync(new BillerDto { Name = "GAS NATURAL", IsActive = true });
        await dup.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ya existe un convenio llamado*");

        var created = await bills.SaveBillerAsync(new BillerDto { Name = "Telefonía fija", CommissionFixed = 1000m, IsActive = false, SortOrder = 9 });
        (await bills.GetBillersAsync()).Should().NotContain(x => x.Id == created.Id);
        (await bills.GetBillersAsync(includeInactive: true)).Should().Contain(x => x.Id == created.Id);
    }

    [Fact]
    public async Task BillerDuplicateCheck_IsCaseInsensitive_ForAccentedNames_OnSqlite()
    {
        await using var db = NewContext();
        await DbInitializer.InitializeAsync(db);
        var bills = new BillPaymentService(db, _clock);

        // Seeded "Energía": the uppercase accented spelling must be detected as the same convenio.
        var act = () => bills.SaveBillerAsync(new BillerDto { Name = "ENERGÍA", IsActive = true });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Ya existe un convenio llamado*");
    }
}
