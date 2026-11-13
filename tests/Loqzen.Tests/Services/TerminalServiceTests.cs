using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>
/// Terminal registry on real SQLite (unique code index): registration/upsert, legacy adoption, listing order,
/// heartbeat rules and the satellite's catalog-pull stamp.
/// </summary>
public class TerminalServiceTests : IDisposable
{
    private readonly SqliteDb _sqlite = SqliteDb.Create();
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();

    public TerminalServiceTests() => _db = _sqlite.NewContext();

    public void Dispose()
    {
        _db.Dispose();
        _sqlite.Dispose();
    }

    private TerminalService Service(Core.Application.Services.ITerminalContext? ctx = null) => new(_db, _clock, ctx);

    private static DateTime Now(TestClock c) => c.GetUtcNow().UtcDateTime;

    // ── EnsureRegistered ─────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureRegistered_NormalizesCode_TrimAndUpper()
    {
        var dto = await Service().EnsureRegisteredAsync(" caja-02 ", "Caja dos", TerminalKind.Satellite, "1.2.3");

        dto.Code.Should().Be("CAJA-02");
        dto.Name.Should().Be("Caja dos");
        dto.Kind.Should().Be(TerminalKind.Satellite);
        dto.IsActive.Should().BeTrue();
        dto.AppVersion.Should().Be("1.2.3");
        dto.LastSeenAt.Should().Be(Now(_clock));
        dto.OpenShiftNumber.Should().BeNull();

        var row = await _db.Terminals.AsNoTracking().SingleAsync();
        row.Code.Should().Be("CAJA-02", "stored normalized so lookups are plain equality");
    }

    [Fact]
    public async Task EnsureRegistered_IsIdempotent_UpsertsByCode_KeepsId()
    {
        var first = await Service().EnsureRegisteredAsync("caja-02", "Caja dos", TerminalKind.Satellite, "1.0");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await Service().EnsureRegisteredAsync("CAJA-02 ", "Caja dos (renombrada)", TerminalKind.Satellite, "1.1");

        second.Id.Should().Be(first.Id);
        second.Name.Should().Be("Caja dos (renombrada)");
        second.AppVersion.Should().Be("1.1");
        second.LastSeenAt.Should().Be(Now(_clock));
        (await _db.Terminals.CountAsync()).Should().Be(1);
        (await _db.Terminals.SingleAsync()).UpdatedAt.Should().Be(Now(_clock));
    }

    [Fact]
    public async Task EnsureRegistered_BlankName_FallsBackToCode_AndTruncatesLongName()
    {
        var blank = await Service().EnsureRegisteredAsync("caja-09", "   ", TerminalKind.Satellite);
        blank.Name.Should().Be("CAJA-09");

        var longName = new string('x', 80);
        var truncated = await Service().EnsureRegisteredAsync("caja-10", longName, TerminalKind.Satellite);
        truncated.Name.Should().HaveLength(60);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")] // 21 chars
    public async Task EnsureRegistered_InvalidCode_Throws(string code)
    {
        var act = () => Service().EnsureRegisteredAsync(code, "x", TerminalKind.Master);
        await act.Should().ThrowAsync<ArgumentException>();
        (await _db.Terminals.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnsureRegistered_Master_AdoptsLegacyRows_LeavesStampedRowsAlone()
    {
        var other = Guid.NewGuid();
        var legacyShift = new CashShift { ShiftNumber = 1, CashierName = "Legacy", Status = CashShiftStatus.Closed };
        var stampedShift = new CashShift { ShiftNumber = 1, CashierName = "Sat", Status = CashShiftStatus.Closed, TerminalId = other, TerminalCode = "CAJA-02" };
        var legacyOrder = new Order { OrderNumber = "ORD-LEGACY", Status = OrderStatus.Completed, CashShiftId = legacyShift.Id };
        var stampedOrder = new Order { OrderNumber = "ORD-SAT", Status = OrderStatus.Completed, TerminalId = other, TerminalCode = "CAJA-02" };
        var legacyBill = new BillPayment { CashShiftId = legacyShift.Id, BillerName = "Energia", AccountReference = "1", Amount = 10m, ReceiptNumber = "REC-000001", UserName = "u", PaidAt = Now(_clock) };
        var stampedBill = new BillPayment { CashShiftId = stampedShift.Id, BillerName = "Energia", AccountReference = "2", Amount = 10m, ReceiptNumber = "REC-000002", UserName = "u", PaidAt = Now(_clock), TerminalId = other, TerminalCode = "CAJA-02" };
        _db.AddRange(legacyShift, stampedShift, legacyOrder, stampedOrder, legacyBill, stampedBill);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var master = await Service().EnsureRegisteredAsync("caja-01", "Principal", TerminalKind.Master);

        await using var check = _sqlite.NewContext();
        var s = await check.CashShifts.SingleAsync(x => x.Id == legacyShift.Id);
        s.TerminalId.Should().Be(master.Id);
        s.TerminalCode.Should().Be("CAJA-01");
        var o = await check.Orders.SingleAsync(x => x.Id == legacyOrder.Id);
        o.TerminalId.Should().Be(master.Id);
        o.TerminalCode.Should().Be("CAJA-01");
        var b = await check.BillPayments.SingleAsync(x => x.Id == legacyBill.Id);
        b.TerminalId.Should().Be(master.Id);
        b.TerminalCode.Should().Be("CAJA-01");

        (await check.CashShifts.SingleAsync(x => x.Id == stampedShift.Id)).TerminalId.Should().Be(other);
        (await check.Orders.SingleAsync(x => x.Id == stampedOrder.Id)).TerminalCode.Should().Be("CAJA-02");
        (await check.BillPayments.SingleAsync(x => x.Id == stampedBill.Id)).TerminalCode.Should().Be("CAJA-02");

        (await check.CashShifts.CountAsync(x => x.TerminalId == null)).Should().Be(0);
        (await check.Orders.CountAsync(x => x.TerminalId == null)).Should().Be(0);
        (await check.BillPayments.CountAsync(x => x.TerminalId == null)).Should().Be(0);
    }

    [Fact]
    public async Task EnsureRegistered_Satellite_DoesNotAdoptLegacyRows()
    {
        var legacyShift = new CashShift { ShiftNumber = 1, CashierName = "Legacy", Status = CashShiftStatus.Closed };
        var legacyOrder = new Order { OrderNumber = "ORD-LEGACY", Status = OrderStatus.Completed };
        var legacyBill = new BillPayment { CashShiftId = legacyShift.Id, BillerName = "Energia", AccountReference = "1", Amount = 10m, ReceiptNumber = "REC-000001", UserName = "u", PaidAt = Now(_clock) };
        _db.AddRange(legacyShift, legacyOrder, legacyBill);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Service().EnsureRegisteredAsync("caja-02", "Dos", TerminalKind.Satellite);

        await using var check = _sqlite.NewContext();
        (await check.CashShifts.SingleAsync()).TerminalId.Should().BeNull();
        (await check.Orders.SingleAsync()).TerminalId.Should().BeNull();
        (await check.BillPayments.SingleAsync()).TerminalId.Should().BeNull();
    }

    [Fact]
    public async Task EnsureRegistered_ReturnsOpenShiftOfThatTerminal()
    {
        var master = await Service().EnsureRegisteredAsync("caja-01", "Principal", TerminalKind.Master);
        _db.CashShifts.Add(new CashShift { ShiftNumber = 7, CashierName = "Ana", Status = CashShiftStatus.Open, TerminalId = master.Id, TerminalCode = "CAJA-01" });
        _db.CashShifts.Add(new CashShift { ShiftNumber = 3, CashierName = "Otro", Status = CashShiftStatus.Open, TerminalId = Guid.NewGuid(), TerminalCode = "CAJA-02" });
        await _db.SaveChangesAsync();

        var again = await Service().EnsureRegisteredAsync("caja-01", "Principal", TerminalKind.Master);
        again.OpenShiftNumber.Should().Be(7);
        again.OpenShiftCashier.Should().Be("Ana");
    }

    // ── GetAll / GetByCode ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_MasterFirst_ThenByCode_WithOpenShiftColumns()
    {
        var c03 = new Terminal { Code = "CAJA-03", Name = "Tres", Kind = TerminalKind.Satellite };
        var c02 = new Terminal { Code = "CAJA-02", Name = "Dos", Kind = TerminalKind.Satellite };
        var c01 = new Terminal { Code = "CAJA-01", Name = "Principal", Kind = TerminalKind.Master };
        var zz = new Terminal { Code = "ZZ", Name = "Z", Kind = TerminalKind.Satellite };
        _db.Terminals.AddRange(c03, c02, c01, zz);
        _db.CashShifts.AddRange(
            new CashShift { ShiftNumber = 4, CashierName = "Ana", Status = CashShiftStatus.Open, TerminalId = c02.Id, TerminalCode = "CAJA-02" },
            new CashShift { ShiftNumber = 3, CashierName = "Viejo", Status = CashShiftStatus.Closed, TerminalId = c02.Id, TerminalCode = "CAJA-02" },
            new CashShift { ShiftNumber = 9, CashierName = "Cerrado", Status = CashShiftStatus.Closed, TerminalId = c03.Id, TerminalCode = "CAJA-03" },
            new CashShift { ShiftNumber = 1, CashierName = "Legacy", Status = CashShiftStatus.Open });
        await _db.SaveChangesAsync();

        var all = await Service().GetAllAsync();

        all.Select(t => t.Code).Should().Equal("CAJA-01", "CAJA-02", "CAJA-03", "ZZ");
        all[0].Kind.Should().Be(TerminalKind.Master);
        all[0].KindLabel.Should().Be("Principal");
        all[1].KindLabel.Should().Be("Satélite");

        all[1].OpenShiftNumber.Should().Be(4);
        all[1].OpenShiftCashier.Should().Be("Ana");
        all[0].OpenShiftNumber.Should().BeNull("legacy open rows are not attributed by GetAll (adoption stamps them at start-up)");
        all[2].OpenShiftNumber.Should().BeNull("closed shifts do not count");
        all[3].OpenShiftNumber.Should().BeNull();
    }

    [Fact]
    public async Task GetByCode_Normalizes_AndReturnsNullForUnknownOrInvalid()
    {
        await Service().EnsureRegisteredAsync("caja-02", "Dos", TerminalKind.Satellite);

        (await Service().GetByCodeAsync(" caja-02 "))!.Code.Should().Be("CAJA-02");
        (await Service().GetByCodeAsync("CAJA-99")).Should().BeNull();
        (await Service().GetByCodeAsync("x")).Should().BeNull("invalid codes are not an error here");
        (await Service().GetByCodeAsync("")).Should().BeNull();
    }

    [Fact]
    public void TerminalDto_IsOnline_UsesWindow()
    {
        var now = Now(_clock);
        new TerminalDto { LastSeenAt = now.AddSeconds(-30) }.IsOnline(now, TimeSpan.FromMinutes(1)).Should().BeTrue();
        new TerminalDto { LastSeenAt = now.AddSeconds(-90) }.IsOnline(now, TimeSpan.FromMinutes(1)).Should().BeFalse();
        new TerminalDto { LastSeenAt = null }.IsOnline(now, TimeSpan.FromMinutes(1)).Should().BeFalse();
    }

    // ── Heartbeat ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_RejectsOwnCode()
    {
        var me = Terminals.Master("CAJA-01");
        var act = () => Service(me).HeartbeatAsync(new TerminalHeartbeatRequest(" caja-01 ", "Impostor", TerminalKind.Satellite), "10.0.0.5");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CAJA-01*");
        (await _db.Terminals.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Heartbeat_RejectsKindMaster()
    {
        var act = () => Service(Terminals.Master()).HeartbeatAsync(new TerminalHeartbeatRequest("CAJA-05", "Otra principal", TerminalKind.Master), null);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _db.Terminals.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Heartbeat_OnSatelliteContext_Throws()
    {
        var act = () => Service(Terminals.Satellite("CAJA-02")).HeartbeatAsync(new TerminalHeartbeatRequest("CAJA-03", "Tres", TerminalKind.Satellite), null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Heartbeat_UpsertsSatellite_SetsLastSeenAddressVersion()
    {
        var svc = Service(Terminals.Master("CAJA-01"));

        var first = await svc.HeartbeatAsync(new TerminalHeartbeatRequest("caja-02", "Caja dos", TerminalKind.Satellite, "1.0"), "192.168.1.22");
        first.Code.Should().Be("CAJA-02");
        first.Kind.Should().Be(TerminalKind.Satellite);
        first.LastSeenAt.Should().Be(Now(_clock));
        first.LastAddress.Should().Be("192.168.1.22");
        first.AppVersion.Should().Be("1.0");
        first.LastSyncAt.Should().BeNull("a heartbeat is not a sync");

        _clock.Advance(TimeSpan.FromSeconds(30));
        var second = await svc.HeartbeatAsync(new TerminalHeartbeatRequest("CAJA-02", "Caja dos B", TerminalKind.Satellite, "1.1"), new string('9', 100));

        second.Id.Should().Be(first.Id);
        second.Name.Should().Be("Caja dos B");
        second.AppVersion.Should().Be("1.1");
        second.LastSeenAt.Should().Be(Now(_clock));
        second.LastAddress.Should().HaveLength(64, "address is truncated to the column size");
        (await _db.Terminals.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Heartbeat_ReturnsOpenShiftReplicatedForThatSatellite()
    {
        var svc = Service(Terminals.Master("CAJA-01"));
        var sat = await svc.HeartbeatAsync(new TerminalHeartbeatRequest("CAJA-02", "Dos", TerminalKind.Satellite), null);
        _db.CashShifts.Add(new CashShift { ShiftNumber = 12, CashierName = "Luis", Status = CashShiftStatus.Open, TerminalId = sat.Id, TerminalCode = "CAJA-02" });
        await _db.SaveChangesAsync();

        var again = await svc.HeartbeatAsync(new TerminalHeartbeatRequest("CAJA-02", "Dos", TerminalKind.Satellite), null);
        again.OpenShiftNumber.Should().Be(12);
        again.OpenShiftCashier.Should().Be("Luis");
    }

    // ── MarkCatalogPulled ────────────────────────────────────────────────────

    [Fact]
    public async Task MarkCatalogPulled_StampsOwnRow()
    {
        var registered = await Service().EnsureRegisteredAsync("caja-02", "Dos", TerminalKind.Satellite);
        var ctx = new Core.Application.Services.TerminalContext();
        ctx.Set(registered);
        _clock.Advance(TimeSpan.FromMinutes(1));

        await Service(ctx).MarkCatalogPulledAsync();

        var row = await _db.Terminals.AsNoTracking().SingleAsync();
        row.LastCatalogPullAt.Should().Be(Now(_clock));
        row.LastSeenAt.Should().Be(Now(_clock));
        row.UpdatedAt.Should().Be(Now(_clock));
    }

    [Fact]
    public async Task MarkCatalogPulled_WithoutContext_OrUnregistered_Throws()
    {
        var noCtx = () => Service().MarkCatalogPulledAsync();
        await noCtx.Should().ThrowAsync<InvalidOperationException>();

        var unset = () => Service(new Core.Application.Services.TerminalContext()).MarkCatalogPulledAsync();
        await unset.Should().ThrowAsync<InvalidOperationException>();

        var unregistered = () => Service(Terminals.Satellite("CAJA-77")).MarkCatalogPulledAsync();
        await unregistered.Should().ThrowAsync<InvalidOperationException>();
    }
}
