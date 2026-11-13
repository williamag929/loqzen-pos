using System.Text.Json;
using Core.Application.DTOs;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static Loqzen.Tests.Fixtures.CashOpsData;

namespace Loqzen.Tests.Services;

/// <summary>Close with denomination count, difference threshold + manager approval, float, history.</summary>
public class CloseShiftTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly CashShiftService _service;

    public CloseShiftTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new CashShiftService(_db, _clock);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Open shift with expected cash = 150.000 (100.000 opening + 50.000 cash sale).</summary>
    private async Task<Guid> OpenWithExpected150kAsync()
    {
        var shift = await _service.OpenShiftAsync("Ana", 100000m);
        AddSale(_db, shift.Id, PaymentMethod.Cash, 50000m);
        AddSale(_db, shift.Id, PaymentMethod.Card, 80000m);
        _clock.Advance(TimeSpan.FromHours(8));
        return shift.Id;
    }

    private async Task AssertStillOpenAndUntouched(Guid shiftId)
    {
        var s = await _db.CashShifts.AsNoTracking().SingleAsync(x => x.Id == shiftId);
        s.Status.Should().Be(CashShiftStatus.Open);
        s.ClosedAt.Should().BeNull();
        (await _db.CashCountLines.CountAsync()).Should().Be(0);
        (await _db.OutboxMessages.CountAsync(o => o.EventType == "CashShiftClosed")).Should().Be(0);
    }

    [Fact]
    public async Task ExactMatch_ClosesWithoutPin()
    {
        var id = await OpenWithExpected150kAsync();

        var r = await _service.CloseShiftAsync(Close(150000m, floatLeft: 100000m));

        r.Status.Should().Be(CashShiftStatus.Closed);
        r.ExpectedCash.Should().Be(150000m);
        r.CountedCash.Should().Be(150000m);
        r.Difference.Should().Be(0m);
        r.DifferenceApprovedBy.Should().BeNull();
        var s = await _db.CashShifts.AsNoTracking().SingleAsync(x => x.Id == id);
        s.ExpectedAmount.Should().Be(150000m);
        s.ClosingAmount.Should().Be(150000m);
        s.DifferenceOverrideId.Should().BeNull();
    }

    [Theory]
    [InlineData(155000)] // +5000 sobrante
    [InlineData(145000)] // -5000 faltante
    public async Task DifferenceEqualToThreshold_ClosesWithoutPin(int counted)
    {
        await OpenWithExpected150kAsync();
        var r = await _service.CloseShiftAsync(Close(counted));
        r.Status.Should().Be(CashShiftStatus.Closed);
        Math.Abs(r.Difference!.Value).Should().Be(5000m);
    }

    [Theory]
    [InlineData(155001, 5001)]
    [InlineData(144999, -5001)]
    public async Task DifferenceOverThreshold_WithoutOverride_Throws_AndSavesNothing(int counted, int diff)
    {
        var id = await OpenWithExpected150kAsync();

        var act = () => _service.CloseShiftAsync(Close(counted, notes: "Nota"));

        var ex = (await act.Should().ThrowAsync<CashDifferenceRequiresApprovalException>()).Which;
        ex.Difference.Should().Be(diff);
        ex.Threshold.Should().Be(5000m);
        ex.Message.Should().Contain("autorización de gerente");
        await AssertStillOpenAndUntouched(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidOverride_ButBlankNotes_StillThrows_AndOverrideNotConsumed(string? notes)
    {
        var id = await OpenWithExpected150kAsync();
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.CashDifference);

        var act = () => _service.CloseShiftAsync(Close(120000m, notes: notes, overrideId: oid));

        await act.Should().ThrowAsync<CashDifferenceRequiresApprovalException>();
        await AssertStillOpenAndUntouched(id);
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync()).ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task ValidOverride_AndNotes_Closes_ConsumesOverride_RecordsApprover()
    {
        var id = await OpenWithExpected150kAsync();
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.CashDifference, "Gerente Marta");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var r = await _service.CloseShiftAsync(Close(120000m, notes: "  Billete falso  ", overrideId: oid));

        r.Status.Should().Be(CashShiftStatus.Closed);
        r.Difference.Should().Be(-30000m);
        r.DifferenceApprovedBy.Should().Be("Gerente Marta");
        r.Notes.Should().Be("Billete falso");
        var o = await _db.ManagerOverrides.AsNoTracking().SingleAsync();
        o.ConsumedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        o.ConsumedFor.Should().Be($"CashShift {id}");
        (await _db.CashShifts.AsNoTracking().SingleAsync()).DifferenceOverrideId.Should().Be(oid);
    }

    [Fact]
    public async Task OverrideNotNeeded_IsIgnored_AndNotConsumed()
    {
        await OpenWithExpected150kAsync();
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.CashDifference);

        var r = await _service.CloseShiftAsync(Close(150000m, overrideId: oid));

        r.DifferenceApprovedBy.Should().BeNull();
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync()).ConsumedAt.Should().BeNull();
        (await _db.CashShifts.AsNoTracking().SingleAsync()).DifferenceOverrideId.Should().BeNull();
    }

    [Theory]
    [InlineData(AuthorizedAction.CashMovement)]
    [InlineData(AuthorizedAction.CloseShift)]
    public async Task WrongActionOverride_RequiresAuthorization(AuthorizedAction action)
    {
        var id = await OpenWithExpected150kAsync();
        var oid = await AddOverrideAsync(_db, _clock, action);

        var act = () => _service.CloseShiftAsync(Close(120000m, notes: "Nota", overrideId: oid));

        var ex = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        ex.Which.Should().NotBeOfType<CashDifferenceRequiresApprovalException>();
        await AssertStillOpenAndUntouched(id);
    }

    [Fact]
    public async Task ConsumedOverride_RequiresAuthorization()
    {
        await OpenWithExpected150kAsync();
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.CashDifference);
        var o = await _db.ManagerOverrides.SingleAsync();
        o.ConsumedAt = _clock.GetUtcNow().UtcDateTime;
        o.ConsumedFor = "otra cosa";
        await _db.SaveChangesAsync();

        var act = () => _service.CloseShiftAsync(Close(120000m, notes: "Nota", overrideId: oid));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
    }

    [Theory]
    [InlineData(150001)]
    [InlineData(-1)]
    public async Task FloatOutsideZeroToCounted_Throws(int floatLeft)
    {
        var id = await OpenWithExpected150kAsync();
        var act = () => _service.CloseShiftAsync(Close(150000m, floatLeft: floatLeft));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*fondo*");
        await AssertStillOpenAndUntouched(id);
    }

    [Fact]
    public async Task FloatEqualToCounted_IsAllowed()
    {
        await OpenWithExpected150kAsync();
        var r = await _service.CloseShiftAsync(Close(150000m, floatLeft: 150000m));
        r.FloatLeftAmount.Should().Be(150000m);
    }

    [Theory]
    [InlineData(ComandaStatus.Open)]
    [InlineData(ComandaStatus.ReadyToPay)]
    public async Task OpenComandas_BlockClose(ComandaStatus status)
    {
        var id = await OpenWithExpected150kAsync();
        _db.Comandas.Add(new Core.Domain.Entities.Comanda { Number = 7, TableName = "Mesa 7", Status = status });
        await _db.SaveChangesAsync();

        var act = () => _service.CloseShiftAsync(Close(150000m));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*comanda*");
        await AssertStillOpenAndUntouched(id);
    }

    [Fact]
    public async Task NoOpenShift_Throws()
    {
        var act = () => _service.CloseShiftAsync(Close(0m));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turno abierto*");
    }

    [Fact]
    public async Task NegativeQuantity_Throws()
    {
        await OpenWithExpected150kAsync();
        var req = new CloseShiftRequest(new[] { new CashCountLineDto { Denomination = 1000m, Quantity = -1 } }, 0m, null, null, "Ana", true);
        var act = () => _service.CloseShiftAsync(req);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ClosedBy_Blind_Float_AndCountLines_ArePersisted()
    {
        var id = await OpenWithExpected150kAsync();
        var userId = Guid.NewGuid();
        var lines = new[]
        {
            new CashCountLineDto { Denomination = 100000m, Quantity = 1 },
            new CashCountLineDto { Denomination = 20000m, Quantity = 2 },
            new CashCountLineDto { Denomination = 5000m, Quantity = 2 },
            new CashCountLineDto { Denomination = 500m, Quantity = 0 },
        };

        await _service.CloseShiftAsync(new CloseShiftRequest(lines, 60000m, null, userId, "  Carlos  ", WasBlindCount: false));

        var s = await _db.CashShifts.AsNoTracking().SingleAsync(x => x.Id == id);
        s.ClosedByUserId.Should().Be(userId);
        s.ClosedByName.Should().Be("Carlos");
        s.WasBlindCount.Should().BeFalse();
        s.FloatLeftAmount.Should().Be(60000m);
        s.ClosingAmount.Should().Be(150000m);
        s.ClosedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        s.Notes.Should().BeNull();
        (await _db.CashCountLines.CountAsync(l => l.CashShiftId == id)).Should().Be(3, "zero-quantity lines are skipped");
    }

    [Fact]
    public async Task BlankClosedByName_FallsBackToCashier()
    {
        await OpenWithExpected150kAsync();
        var r = await _service.CloseShiftAsync(Close(150000m, closedBy: " "));
        r.ClosedByName.Should().Be("Ana");
    }

    [Fact]
    public async Task Outbox_CashShiftClosed_CarriesDifference()
    {
        var id = await OpenWithExpected150kAsync();

        await _service.CloseShiftAsync(Close(152000m));

        var msg = await _db.OutboxMessages.AsNoTracking().SingleAsync(o => o.EventType == "CashShiftClosed");
        msg.AggregateType.Should().Be("CashShift");
        msg.AggregateId.Should().Be(id);
        msg.Status.Should().Be(SyncStatus.Pending);
        using var doc = JsonDocument.Parse(msg.PayloadJson);
        doc.RootElement.GetProperty("difference").GetDecimal().Should().Be(2000m);
        doc.RootElement.GetProperty("expected").GetDecimal().Should().Be(150000m);
        doc.RootElement.GetProperty("counted").GetDecimal().Should().Be(152000m);
        doc.RootElement.GetProperty("number").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task CustomThreshold_IsHonoured()
    {
        var service = new CashShiftService(_db, _clock, new CashShiftSettings { MaxDifferenceWithoutManager = 0m });
        await service.OpenShiftAsync("Ana", 1000m);
        var act = () => service.CloseShiftAsync(Close(999m));
        (await act.Should().ThrowAsync<CashDifferenceRequiresApprovalException>()).Which.Threshold.Should().Be(0m);
    }

    [Fact]
    public async Task SuggestedOpening_IsDefaultFloat_WhenNoShift_ThenLastFloat()
    {
        var service = new CashShiftService(_db, _clock, new CashShiftSettings { DefaultFloat = 123000m });
        (await service.GetSuggestedOpeningAmountAsync()).Should().Be(123000m);

        await service.OpenShiftAsync("Ana", 100000m);
        await service.CloseShiftAsync(Close(100000m, floatLeft: 80000m));
        (await service.GetSuggestedOpeningAmountAsync()).Should().Be(80000m);

        await service.OpenShiftAsync("Beto", 80000m);
        await service.CloseShiftAsync(Close(80000m, floatLeft: 30000m));
        (await service.GetSuggestedOpeningAmountAsync()).Should().Be(30000m, "the newest close wins");
    }

    [Fact]
    public async Task SuggestedOpening_AfterLegacyClose_UsesDefault()
    {
        await _service.OpenShiftAsync("Ana", 100000m);
        await _service.CloseShiftAsync(100000m); // legacy API: no float recorded
        (await _service.GetSuggestedOpeningAmountAsync()).Should().Be(new CashShiftSettings().DefaultFloat);
    }

    [Fact]
    public async Task History_NewestFirst_WithManagerApprovalFlag()
    {
        await _service.OpenShiftAsync("Ana", 100000m);
        await _service.CloseShiftAsync(Close(100000m));

        await _service.OpenShiftAsync("Beto", 50000m);
        var oid = await AddOverrideAsync(_db, _clock, AuthorizedAction.CashDifference);
        await _service.CloseShiftAsync(Close(40000m, notes: "Faltante", overrideId: oid));

        await _service.OpenShiftAsync("Caro", 0m); // open: not in history

        var h = await _service.GetHistoryAsync();

        h.Select(x => x.ShiftNumber).Should().Equal(2, 1);
        h[0].CashierName.Should().Be("Beto");
        h[0].HadManagerApproval.Should().BeTrue();
        h[0].ExpectedAmount.Should().Be(50000m);
        h[0].ClosingAmount.Should().Be(40000m);
        h[0].Difference.Should().Be(-10000m);
        h[1].HadManagerApproval.Should().BeFalse();
        (await _service.GetHistoryAsync(take: 1)).Should().ContainSingle().Which.ShiftNumber.Should().Be(2);
    }

    [Fact]
    public async Task PartialCount_StoresExpectedAndCounted_AndDoesNotClose()
    {
        var id = await OpenWithExpected150kAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));

        var p = await _service.RecordPartialCountAsync(new[]
        {
            new CashCountLineDto { Denomination = 50000m, Quantity = 2 },
            new CashCountLineDto { Denomination = 10000m, Quantity = 4 },
        }, "Supervisor");

        p.ExpectedAmount.Should().Be(150000m);
        p.CountedAmount.Should().Be(140000m);
        p.Difference.Should().Be(-10000m);
        p.UserName.Should().Be("Supervisor");
        p.CountedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);

        var stored = await _db.PartialCashCounts.AsNoTracking().SingleAsync();
        stored.CashShiftId.Should().Be(id);
        stored.LinesJson.Should().Contain("50000").And.Contain("quantity");
        (await _db.CashShifts.AsNoTracking().SingleAsync()).Status.Should().Be(CashShiftStatus.Open);
        (await _db.CashCountLines.CountAsync()).Should().Be(0, "partial counts do not write close lines");
    }

    [Fact]
    public async Task PartialCount_WithoutOpenShift_Throws()
    {
        var act = () => _service.RecordPartialCountAsync(Count(1000m), "X");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turno abierto*");
    }

    [Fact]
    public async Task OpenShift_WithUser_RecordsOpenedBy_AndRejectsNegative()
    {
        var uid = Guid.NewGuid();
        var s = await _service.OpenShiftAsync("Ana", 1000m, uid);
        s.OpenedByUserId.Should().Be(uid);
        s.OpenedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);

        var dup = () => _service.OpenShiftAsync("Beto", 0m, null);
        await dup.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ya existe un turno abierto*");
    }

    [Fact]
    public async Task OpenShift_NegativeAmount_Throws()
    {
        var act = () => _service.OpenShiftAsync("Ana", -1m, null);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
