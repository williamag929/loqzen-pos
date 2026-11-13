using Core.Application.DTOs;
using Core.Application.Receipts;
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
/// Cierre del día across registers on real SQLite: one TerminalDayDto per caja (master first), sums that follow the
/// ShiftReconciliationDto rule, open shifts flagged, shifts of other local days excluded, and the printed report.
/// </summary>
public class DayConsolidationTests : IDisposable
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.CreateCustomTimeZone("Test/Bogota", TimeSpan.FromHours(-5), "Bogota", "Bogota");
    private static readonly DateOnly Day = new(2026, 11, 12);

    private readonly SqliteDb _sqlite = SqliteDb.Create();
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 12, 13, 0, 0, TimeSpan.Zero), Bogota); // 08:00 Bogotá
    private readonly CashShiftSettings _settings = new() { ManagerRequiredFor = new List<string>() };
    private readonly TerminalContext _master = Terminals.Master("CAJA-01", "Caja principal");
    private readonly CashShiftService _m;
    private Terminal _caja02 = null!;

    public DayConsolidationTests()
    {
        _db = _sqlite.NewContext();
        _m = new CashShiftService(_db, _clock, _settings, _master);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqlite.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Replicated CAJA-02 rows, inserted as the sync inbox would leave them.</summary>
    private CashShift SatShift(int number, DateTime openedAt, CashShiftStatus status, decimal opening = 0m, decimal expected = 0m, decimal closing = 0m)
    {
        var s = new CashShift
        {
            ShiftNumber = number, CashierName = "Luis", OpenedAt = openedAt, Status = status, OpeningAmount = opening,
            ExpectedAmount = expected, ClosingAmount = closing, ClosedAt = status == CashShiftStatus.Closed ? openedAt.AddHours(8) : null,
            TerminalId = _caja02.Id, TerminalCode = _caja02.Code
        };
        _db.CashShifts.Add(s);
        _db.SaveChanges();
        return s;
    }

    private async Task SeedTerminalsAsync()
    {
        await new TerminalService(_db, _clock, _master).EnsureRegisteredAsync(_master.Code, _master.Name, TerminalKind.Master);
        _caja02 = new Terminal { Code = "CAJA-02", Name = "Caja dos", Kind = TerminalKind.Satellite };
        _db.Terminals.Add(_caja02);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// CAJA-01: shift #1 (closed, +1.000 over) with sales cash 50.000 / card 45.000 / transfer 15.000, recaudo cash 50.000
    /// (+1.500), PaidIn 10.000, PaidOut 5.000, Expense 3.000; shift #2 (open) with one cash sale 7.000.
    /// CAJA-02: shift #1 (closed, −2.000) with cash 12.000 / card 8.000, recaudo card 20.000 (+600), SafeDrop 10.000, TipPayout 2.000.
    /// Out of the day: CAJA-02 shift opened the day before (cash 99.999) and one opened the day after (cash 88.888).
    /// </summary>
    private async Task<(Guid m1, Guid m2, Guid s1)> BuildDayAsync()
    {
        await SeedTerminalsAsync();
        var energia = await AddBillerAsync(_db, "Energia", fixedCommission: 1500m);
        var bills = new BillPaymentService(_db, _clock, _master);

        // CAJA-01 #1
        var m1 = await _m.OpenShiftAsync("Ana", 100000m);
        AddSale(_db, m1.Id, PaymentMethod.Cash, 30000m);
        AddSale(_db, m1.Id, PaymentMethod.Cash, 20000m);
        AddSale(_db, m1.Id, PaymentMethod.Card, 45000m);
        AddSale(_db, m1.Id, PaymentMethod.Transfer, 15000m);
        AddSale(_db, m1.Id, PaymentMethod.Cash, 77777m, OrderStatus.Cancelled);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await bills.RecordAsync(new BillPaymentRequest(energia.Id, "CTA-1", 50000m, PaymentMethod.Cash, 60000m, null, "Ana"));
        await _m.RecordMovementAsync(Movement(CashMovementType.PaidIn, 10000m, "Sencillo"));
        await _m.RecordMovementAsync(Movement(CashMovementType.PaidOut, 5000m, "Hielo"));
        await _m.RecordMovementAsync(Movement(CashMovementType.Expense, 3000m, "Bolsas"));
        _clock.Advance(TimeSpan.FromHours(6));
        var expected = (await _m.GetReconciliationAsync()).ExpectedCash; // 100000 + 50000 + 51500 + 10000 - 5000 - 3000 = 203500
        expected.Should().Be(203500m);
        await _m.CloseShiftAsync(Close(expected + 1000m, floatLeft: 100000m));

        // CAJA-01 #2 (open)
        _clock.Advance(TimeSpan.FromMinutes(5)); // ~14:06 Bogotá
        var m2 = await _m.OpenShiftAsync("Pedro", 100000m);
        AddSale(_db, m2.Id, PaymentMethod.Cash, 7000m);

        // CAJA-02 #1 (replicated, closed, short 2.000)
        // Expected cash = 50.000 + 12.000 (cash sale) − 10.000 (safe drop) − 2.000 (tips) = 50.000; counted 48.000 → −2.000.
        var s1 = SatShift(1, Day.ToDateTime(new TimeOnly(9, 0)).AddHours(5), CashShiftStatus.Closed, opening: 50000m, expected: 50000m, closing: 48000m);
        AddSale(_db, s1.Id, PaymentMethod.Cash, 12000m);
        AddSale(_db, s1.Id, PaymentMethod.Card, 8000m);
        _db.BillPayments.Add(new BillPayment { CashShiftId = s1.Id, TerminalId = _caja02.Id, TerminalCode = "CAJA-02", BillerId = energia.Id, BillerName = "Energia", AccountReference = "CTA-9", Amount = 20000m, Commission = 600m, Method = PaymentMethod.Card, TenderedAmount = 20600m, ReceiptNumber = "CAJA-02:REC-000001", Status = BillPaymentStatus.Completed, UserName = "Luis", PaidAt = s1.OpenedAt.AddHours(1) });
        _db.BillPayments.Add(new BillPayment { CashShiftId = s1.Id, TerminalId = _caja02.Id, TerminalCode = "CAJA-02", BillerId = energia.Id, BillerName = "Energia", AccountReference = "CTA-8", Amount = 5000m, Commission = 1500m, Method = PaymentMethod.Cash, TenderedAmount = 6500m, ReceiptNumber = "CAJA-02:REC-000002", Status = BillPaymentStatus.Voided, UserName = "Luis", PaidAt = s1.OpenedAt.AddHours(2), VoidedAt = s1.OpenedAt.AddHours(3) });
        _db.CashMovements.AddRange(
            new CashMovement { CashShiftId = s1.Id, Type = CashMovementType.SafeDrop, Amount = 10000m, Reason = "Sobre", UserName = "Luis", OccurredAt = s1.OpenedAt.AddHours(4) },
            new CashMovement { CashShiftId = s1.Id, Type = CashMovementType.TipPayout, Amount = 2000m, Reason = "Propinas", UserName = "Luis", OccurredAt = s1.OpenedAt.AddHours(5) });
        await _db.SaveChangesAsync();

        // Out of the local day: opened 2026-11-11 23:30 Bogotá (= 2026-11-12 04:30Z) and 2026-11-13 00:10 Bogotá (= 05:10Z).
        var before = SatShift(0, new DateTime(2026, 11, 12, 4, 30, 0, DateTimeKind.Utc), CashShiftStatus.Closed);
        AddSale(_db, before.Id, PaymentMethod.Cash, 99999m);
        var after = SatShift(2, new DateTime(2026, 11, 13, 5, 10, 0, DateTimeKind.Utc), CashShiftStatus.Open);
        AddSale(_db, after.Id, PaymentMethod.Cash, 88888m);

        return (m1.Id, m2.Id, s1.Id);
    }

    [Fact]
    public async Task TwoTerminals_OneBlockEach_MasterFirst_SumsAndTotals()
    {
        var (m1, m2, s1) = await BuildDayAsync();

        var d = await _m.GetDayConsolidationAsync(Day);

        d.Day.Should().Be(Day);
        d.FromUtc.Should().Be(new DateTime(2026, 11, 12, 5, 0, 0, DateTimeKind.Utc));
        d.ToUtc.Should().Be(new DateTime(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc));
        d.Terminals.Should().HaveCount(2);
        d.Terminals.Select(t => t.TerminalCode).Should().Equal("CAJA-01", "CAJA-02");

        var c1 = d.Terminals[0];
        c1.TerminalId.Should().Be(_master.Id);
        c1.TerminalName.Should().Be("Caja principal");
        c1.ShiftCount.Should().Be(2);
        c1.OpenShiftCount.Should().Be(1);
        c1.OrderCount.Should().Be(5, "cancelled orders are excluded");
        c1.CashSales.Should().Be(57000m);
        c1.CardSales.Should().Be(45000m);
        c1.TransferSales.Should().Be(15000m);
        c1.OtherSales.Should().Be(0m);
        c1.TotalSales.Should().Be(117000m);
        c1.BillPaymentCount.Should().Be(1);
        c1.BillPaymentsTotal.Should().Be(50000m);
        c1.BillPaymentCommission.Should().Be(1500m);
        c1.CashIn.Should().Be(10000m);
        c1.CashOut.Should().Be(8000m);
        c1.Difference.Should().Be(1000m, "only the closed shift counts");
        c1.Shifts.Select(s => s.ShiftNumber).Should().Equal(1, 2);
        c1.Shifts[0].ShiftId.Should().Be(m1);
        c1.Shifts[0].ClosedAt.Should().NotBeNull();
        c1.Shifts[0].Difference.Should().Be(1000m);
        c1.Shifts[0].TerminalCode.Should().Be("CAJA-01");
        c1.Shifts[1].ShiftId.Should().Be(m2);
        c1.Shifts[1].ClosedAt.Should().BeNull("open shift is listed and flagged");

        var c2 = d.Terminals[1];
        c2.TerminalId.Should().Be(_caja02.Id);
        c2.TerminalName.Should().Be("Caja dos");
        c2.ShiftCount.Should().Be(1);
        c2.OpenShiftCount.Should().Be(0);
        c2.OrderCount.Should().Be(2);
        c2.CashSales.Should().Be(12000m);
        c2.CardSales.Should().Be(8000m);
        c2.TransferSales.Should().Be(0m);
        c2.BillPaymentCount.Should().Be(1, "voided recaudos are excluded");
        c2.BillPaymentsTotal.Should().Be(20000m);
        c2.BillPaymentCommission.Should().Be(600m);
        c2.CashIn.Should().Be(0m);
        c2.CashOut.Should().Be(12000m);
        c2.Difference.Should().Be(-2000m);
        c2.Shifts.Should().ContainSingle().Which.ShiftId.Should().Be(s1);

        d.OrderCount.Should().Be(7);
        d.CashSales.Should().Be(69000m);
        d.CardSales.Should().Be(53000m);
        d.TransferSales.Should().Be(15000m);
        d.TotalSales.Should().Be(137000m);
        d.BillPaymentsTotal.Should().Be(70000m);
        d.BillPaymentCommission.Should().Be(2100m);
        d.Difference.Should().Be(-1000m);
        d.OpenShiftCount.Should().Be(1);
    }

    [Fact]
    public async Task Sums_FollowTheShiftReconciliationRule()
    {
        await BuildDayAsync();
        var d = await _m.GetDayConsolidationAsync(Day);

        foreach (var t in d.Terminals)
        {
            var recs = new List<ShiftReconciliationDto>();
            foreach (var s in t.Shifts) recs.Add(await _m.GetReconciliationAsync(s.ShiftId));

            t.CashSales.Should().Be(recs.Sum(r => r.CashSales), t.TerminalCode);
            t.CardSales.Should().Be(recs.Sum(r => r.CardSales), t.TerminalCode);
            t.TransferSales.Should().Be(recs.Sum(r => r.TransferSales), t.TerminalCode);
            t.OtherSales.Should().Be(recs.Sum(r => r.OtherSales), t.TerminalCode);
            t.OrderCount.Should().Be(recs.Sum(r => r.OrderCount), t.TerminalCode);
            t.BillPaymentCount.Should().Be(recs.Sum(r => r.BillPaymentCount), t.TerminalCode);
            t.BillPaymentsTotal.Should().Be(recs.Sum(r => r.BillPaymentsToRemit), t.TerminalCode);
            t.BillPaymentCommission.Should().Be(recs.Sum(r => r.BillPaymentCommissions), t.TerminalCode);
            t.CashIn.Should().Be(recs.Sum(r => r.PaidIn), t.TerminalCode);
            t.CashOut.Should().Be(recs.Sum(r => r.PaidOut + r.Expenses + r.SafeDrops + r.TipPayouts), t.TerminalCode);
            t.Difference.Should().Be(recs.Where(r => r.Status == CashShiftStatus.Closed).Sum(r => r.Difference ?? 0m), t.TerminalCode);
            t.OpenShiftCount.Should().Be(recs.Count(r => r.Status == CashShiftStatus.Open));
        }
    }

    [Fact]
    public async Task ShiftsOpenedOnOtherLocalDays_AreExcluded_LocalTimeZoneRespected()
    {
        await BuildDayAsync();

        var d = await _m.GetDayConsolidationAsync(Day);
        d.Terminals.SelectMany(t => t.Shifts).Select(s => s.ShiftNumber).Should().NotContain(0, "opened 2026-11-11 23:30 Bogotá");
        d.Terminals[1].Shifts.Select(s => s.ShiftNumber).Should().NotContain(2, "opened 2026-11-13 00:10 Bogotá");
        d.CashSales.Should().NotBe(69000m + 99999m);

        var prev = await _m.GetDayConsolidationAsync(Day.AddDays(-1));
        prev.Terminals.Should().ContainSingle().Which.TerminalCode.Should().Be("CAJA-02");
        prev.CashSales.Should().Be(99999m);
        prev.Terminals[0].Shifts.Should().ContainSingle().Which.ShiftNumber.Should().Be(0);

        var next = await _m.GetDayConsolidationAsync(Day.AddDays(1));
        next.Terminals.Should().ContainSingle();
        next.OpenShiftCount.Should().Be(1);
        next.CashSales.Should().Be(88888m);

        // Same data, UTC clock: the 04:30Z shift now belongs to 2026-11-12 and the 05:10Z one to 2026-11-13.
        var utc = new CashShiftService(_db, new TestClock(_clock.GetUtcNow(), TimeZoneInfo.Utc), _settings, _master);
        var utcDay = await utc.GetDayConsolidationAsync(Day);
        utcDay.FromUtc.Should().Be(new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc));
        utcDay.Terminals[1].Shifts.Select(s => s.ShiftNumber).Should().Equal(0, 1);
    }

    [Fact]
    public async Task EmptyDay_HasNoTerminals_AndZeroTotals()
    {
        await SeedTerminalsAsync();
        var d = await _m.GetDayConsolidationAsync(Day);
        d.Terminals.Should().BeEmpty();
        d.TotalSales.Should().Be(0m);
        d.OrderCount.Should().Be(0);
        d.OpenShiftCount.Should().Be(0);
        d.Difference.Should().Be(0m);
    }

    [Fact]
    public async Task LegacyRows_CountAsTheMaster_WhenContextIsSet()
    {
        await SeedTerminalsAsync();
        var legacy = new CashShift { ShiftNumber = 1, CashierName = "Legacy", OpenedAt = Now, Status = CashShiftStatus.Closed, ExpectedAmount = 10m, ClosingAmount = 10m };
        _db.CashShifts.Add(legacy);
        await _db.SaveChangesAsync();
        AddSale(_db, legacy.Id, PaymentMethod.Cash, 4000m);
        _clock.Advance(TimeSpan.FromHours(1));
        var own = await _m.OpenShiftAsync("Ana", 0m);
        AddSale(_db, own.Id, PaymentMethod.Card, 6000m);

        var d = await _m.GetDayConsolidationAsync(Day);

        d.Terminals.Should().ContainSingle();
        var t = d.Terminals[0];
        t.TerminalId.Should().Be(_master.Id);
        t.TerminalCode.Should().Be("CAJA-01");
        t.TerminalName.Should().Be("Caja principal");
        t.ShiftCount.Should().Be(2);
        t.CashSales.Should().Be(4000m);
        t.CardSales.Should().Be(6000m);
        t.Shifts.Select(s => s.ShiftNumber).Should().Equal(1, 2);
    }

    [Fact]
    public async Task LegacyRows_WithoutContext_GoToALocalBucket_ListedFirst()
    {
        await SeedTerminalsAsync();
        var legacy = new CashShift { ShiftNumber = 1, CashierName = "Legacy", OpenedAt = Now, Status = CashShiftStatus.Closed };
        _db.CashShifts.Add(legacy);
        await _db.SaveChangesAsync();
        AddSale(_db, legacy.Id, PaymentMethod.Cash, 4000m);
        var sat = SatShift(1, Now, CashShiftStatus.Closed);
        AddSale(_db, sat.Id, PaymentMethod.Cash, 1000m);

        var d = await new CashShiftService(_db, _clock, _settings).GetDayConsolidationAsync(Day);

        d.Terminals.Select(t => t.TerminalCode).Should().Equal("LOCAL", "CAJA-02");
        d.Terminals[0].TerminalId.Should().BeNull();
        d.Terminals[0].TerminalName.Should().Be("Esta caja");
        d.Terminals[0].CashSales.Should().Be(4000m);
        d.Terminals[1].TerminalName.Should().Be("Caja dos");
    }

    [Fact]
    public async Task UnknownTerminalId_FallsBackToCodeAsName()
    {
        await SeedTerminalsAsync();
        var ghost = new CashShift { ShiftNumber = 1, CashierName = "Ghost", OpenedAt = Now, Status = CashShiftStatus.Closed, TerminalId = Guid.NewGuid(), TerminalCode = "CAJA-77" };
        _db.CashShifts.Add(ghost);
        await _db.SaveChangesAsync();

        var d = await _m.GetDayConsolidationAsync(Day);
        d.Terminals.Should().ContainSingle().Which.TerminalName.Should().Be("CAJA-77");
    }

    // ── printed report ───────────────────────────────────────────────────────

    private static void AssertPrintable(IReadOnlyList<ReceiptLine> lines)
    {
        lines.Should().NotBeEmpty();
        foreach (var l in lines)
        {
            var max = l.IsLarge ? ReceiptFormatter.DefaultWidth / 2 : ReceiptFormatter.DefaultWidth;
            l.Text.Length.Should().BeLessThanOrEqualTo(max, $"line '{l.Text}'");
            l.Text.Length.Should().BeLessThanOrEqualTo(32);
            l.Text.Should().MatchRegex("^[\\x20-\\x7E]*$", $"printer encodes ASCII: '{l.Text}'");
        }
    }

    [Fact]
    public async Task DayConsolidationReport_Header_OneBlockPerCaja_Totals_Ascii32()
    {
        await BuildDayAsync();
        var d = await _m.GetDayConsolidationAsync(Day);
        d.Terminals[1].TerminalName = "Caja dos (Ñandú – pasillo norte, segundo piso)"; // long + non-ASCII

        var lines = CashReports.DayConsolidationReport(d, "Panadería Ñapa del Río");
        AssertPrintable(lines);
        var t = ReceiptFormatter.ToPlainText(lines);

        t.Should().Contain("Panaderia Napa del Rio");
        t.Should().Contain("CIERRE DEL DIA");
        t.Should().Contain("2026-11-12");
        t.Should().Contain("Cajas:   2");
        t.Should().Contain("TURNOS ABIERTOS: 1");

        lines.Count(l => l.IsBold && l.Text.StartsWith("CAJA-0")).Should().Be(2, "one section per caja");
        t.Should().Contain("CAJA-01 - Caja principal");
        t.Should().Contain("CAJA-02 - Caja dos (Nandu");
        t.IndexOf("CAJA-01", StringComparison.Ordinal).Should().BeLessThan(t.IndexOf("CAJA-02", StringComparison.Ordinal), "master first");

        t.Should().Contain("2 (1 abierto)");
        t.Should().Contain("ABIERTO");
        t.Should().Contain("#1 ");
        t.Should().Contain("Pedro");
        t.Should().Contain("Recaudos (1):");
        t.Should().Contain("Entradas:");
        t.Should().Contain("Salidas:");
        t.Should().Contain(ReceiptFormatter.Cop(117000m));
        t.Should().Contain(ReceiptFormatter.Cop(20000m));

        t.Should().Contain("TOTAL DEL DIA");
        t.Should().Contain("VENTAS:");
        t.Should().Contain(ReceiptFormatter.Cop(137000m));
        t.Should().Contain("# Ordenes:");
        t.Should().Contain("Diferencia (faltante):");
        t.Should().Contain("Firma gerente");
        lines.Should().Contain(l => l.IsLarge && l.Text.Contains("VENTAS:"));
    }

    [Fact]
    public void DayConsolidationReport_ExactDifference_NoOpenShifts_BigAmountsFit()
    {
        var d = new DayConsolidationDto
        {
            Day = Day,
            Terminals =
            {
                new TerminalDayDto { TerminalCode = "CAJA-01", TerminalName = "Principal", ShiftCount = 1, CashSales = 123456789m, CardSales = 98765432m, OtherSales = 1m, Difference = 0m,
                    Shifts = { new ShiftHistoryItemDto { ShiftNumber = 123, CashierName = "María José Pérez Gómez de la Torre", OpenedAt = Now, ClosedAt = Now, ExpectedAmount = 5m, ClosingAmount = 5m } } }
            }
        };

        var lines = CashReports.DayConsolidationReport(d, "Tienda");
        AssertPrintable(lines);
        var t = ReceiptFormatter.ToPlainText(lines);
        t.Should().NotContain("TURNOS ABIERTOS");
        t.Should().NotContain("abierto");
        t.Should().Contain("Diferencia (exacta):");
        t.Should().Contain("Otros:");
        t.Should().Contain("Turnos:");
    }

    [Fact]
    public void DayConsolidationReport_Sobrante_WhenPositive()
    {
        var d = new DayConsolidationDto { Day = Day, Terminals = { new TerminalDayDto { TerminalCode = "CAJA-01", TerminalName = "P", Difference = 500m } } };
        ReceiptFormatter.ToPlainText(CashReports.DayConsolidationReport(d, "T")).Should().Contain("Diferencia (sobrante):");
    }

    [Fact]
    public void ZReport_PrintsTerminalLine_OnlyWhenCodeKnown()
    {
        var r = new ShiftReconciliationDto { ShiftNumber = 5, CashierName = "Ana", Status = CashShiftStatus.Closed, OpenedAt = Now, ClosedAt = Now, TerminalCode = "CAJA-02" };
        var with = ReceiptFormatter.ToPlainText(CashReports.ZReport(r, "Tienda"));
        with.Should().Contain("CAJA-02 - Turno #5");
        AssertPrintable(CashReports.ZReport(r, "Tienda"));

        r.TerminalCode = null;
        ReceiptFormatter.ToPlainText(CashReports.ZReport(r, "Tienda")).Should().NotContain("Turno #5\n").And.NotContain(" - Turno #5");
    }
}
