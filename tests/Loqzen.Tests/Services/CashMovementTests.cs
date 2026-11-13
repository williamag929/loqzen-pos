using Core.Application.DTOs;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using static Loqzen.Tests.Fixtures.CashOpsData;

namespace Loqzen.Tests.Services;

/// <summary>Manual cash movements (entradas, salidas, gastos, retiros, propinas) on the open shift.</summary>
public class CashMovementTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly CashShiftService _service;

    public CashMovementTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new CashShiftService(_db, _clock);
    }

    public void Dispose() => _db.Dispose();

    private Task<Guid> OverrideAsync(AuthorizedAction action = AuthorizedAction.CashMovement) => AddOverrideAsync(_db, _clock, action);

    [Fact]
    public async Task PaidIn_WithoutOverride_IsSaved_AndRaisesExpectedCash()
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        _clock.Advance(TimeSpan.FromMinutes(10));

        var dto = await _service.RecordMovementAsync(Movement(CashMovementType.PaidIn, 20000m, "  Sencillo del banco  ", reference: "B-1"));

        dto.Type.Should().Be(CashMovementType.PaidIn);
        dto.Amount.Should().Be(20000m);
        dto.SignedAmount.Should().Be(20000m);
        dto.Reason.Should().Be("Sencillo del banco", "reason is trimmed");
        dto.Reference.Should().Be("B-1");
        dto.ManagerOverrideId.Should().BeNull();
        dto.OccurredAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        (await _service.GetReconciliationAsync()).ExpectedCash.Should().Be(120000m);
        (await _db.OutboxMessages.SingleAsync()).EventType.Should().Be("CashMovementRecorded");
    }

    [Theory]
    [InlineData(CashMovementType.PaidOut)]
    [InlineData(CashMovementType.Expense)]
    [InlineData(CashMovementType.SafeDrop)]
    [InlineData(CashMovementType.TipPayout)]
    public async Task CashOut_WithoutOverride_RequiresManager_AndSavesNothing(CashMovementType type)
    {
        await _service.OpenShiftAsync("Caja", 100000m);

        var act = () => _service.RecordMovementAsync(Movement(type, 10000m));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        (await _db.CashMovements.CountAsync()).Should().Be(0);
        (await _db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(CashMovementType.PaidOut)]
    [InlineData(CashMovementType.Expense)]
    [InlineData(CashMovementType.SafeDrop)]
    [InlineData(CashMovementType.TipPayout)]
    public async Task CashOut_WithValidOverride_IsSaved_ConsumesOverride_AndWritesOutbox(CashMovementType type)
    {
        var shift = await _service.OpenShiftAsync("Caja", 100000m);
        var oid = await OverrideAsync();
        _clock.Advance(TimeSpan.FromMinutes(3));

        var dto = await _service.RecordMovementAsync(Movement(type, 30000m, overrideId: oid));

        dto.ManagerOverrideId.Should().Be(oid);
        dto.SignedAmount.Should().Be(-30000m);
        var stored = await _db.CashMovements.AsNoTracking().SingleAsync();
        stored.CashShiftId.Should().Be(shift.Id);
        stored.ManagerOverrideId.Should().Be(oid);
        var o = await _db.ManagerOverrides.AsNoTracking().SingleAsync(x => x.Id == oid);
        o.ConsumedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        o.ConsumedFor.Should().Be($"CashMovement {dto.Id}");
        var outbox = await _db.OutboxMessages.AsNoTracking().SingleAsync();
        outbox.EventType.Should().Be("CashMovementRecorded");
        outbox.AggregateId.Should().Be(dto.Id);
        outbox.PayloadJson.Should().Contain(type.ToString());
        (await _service.GetReconciliationAsync()).ExpectedCash.Should().Be(70000m);
    }

    [Fact]
    public async Task ReusingConsumedOverride_Fails()
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        var oid = await OverrideAsync();
        var first = await _service.RecordMovementAsync(Movement(CashMovementType.Expense, 5000m, overrideId: oid));

        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.Expense, 5000m, overrideId: oid));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        (await _db.CashMovements.CountAsync()).Should().Be(1);
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync()).ConsumedFor.Should().Be($"CashMovement {first.Id}");
    }

    [Fact]
    public async Task OverrideOfAnotherAction_Fails_AndIsNotConsumed()
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        var oid = await OverrideAsync(AuthorizedAction.OpenCashDrawer);

        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.SafeDrop, 5000m, overrideId: oid));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync()).ConsumedAt.Should().BeNull();
        (await _db.CashMovements.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UnknownOverrideId_Fails()
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.PaidOut, 5000m, overrideId: Guid.NewGuid()));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
    }

    [Fact]
    public async Task OverridePassedForPaidIn_IsIgnored_AndNotConsumed()
    {
        await _service.OpenShiftAsync("Caja", 0m);
        var oid = await OverrideAsync();

        var dto = await _service.RecordMovementAsync(Movement(CashMovementType.PaidIn, 1000m, overrideId: oid));

        dto.ManagerOverrideId.Should().BeNull();
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync()).ConsumedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveAmount_Rejected(int amount)
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.PaidIn, amount));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mayor que cero*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankReason_Rejected(string reason)
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.PaidIn, 1000m, reason));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*motivo*");
    }

    [Fact]
    public async Task ReasonLongerThan200_Rejected_But200Accepted()
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.PaidIn, 1000m, new string('x', 201)));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*200*");

        var ok = await _service.RecordMovementAsync(Movement(CashMovementType.PaidIn, 1000m, new string('x', 200)));
        ok.Reason.Should().HaveLength(200);
    }

    [Theory]
    [InlineData(CashMovementType.BillPayment)]
    [InlineData(CashMovementType.BillPaymentVoid)]
    public async Task NonManualType_Rejected(CashMovementType type)
    {
        await _service.OpenShiftAsync("Caja", 100000m);
        var oid = await OverrideAsync();
        var act = () => _service.RecordMovementAsync(Movement(type, 1000m, overrideId: oid));
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*no permitido*");
        (await _db.CashMovements.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CashOutLargerThanExpected_Rejected_NothingSaved_OverrideNotConsumed()
    {
        var shift = await _service.OpenShiftAsync("Caja", 50000m);
        AddSale(_db, shift.Id, PaymentMethod.Cash, 10000m);
        AddSale(_db, shift.Id, PaymentMethod.Card, 90000m); // card does not count as cash
        var oid = await OverrideAsync();

        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.SafeDrop, 60001m, overrideId: oid));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("El monto (*) supera el efectivo esperado en caja (*)*");
        (await _db.CashMovements.CountAsync()).Should().Be(0);
        (await _db.OutboxMessages.CountAsync()).Should().Be(0);
        (await _db.ManagerOverrides.AsNoTracking().SingleAsync()).ConsumedAt.Should().BeNull();

        // Exactly the expected cash is allowed.
        var dto = await _service.RecordMovementAsync(Movement(CashMovementType.SafeDrop, 60000m, overrideId: oid));
        dto.Amount.Should().Be(60000m);
        (await _service.GetReconciliationAsync()).ExpectedCash.Should().Be(0m);
    }

    [Fact]
    public async Task NoOpenShift_Rejected()
    {
        var act = () => _service.RecordMovementAsync(Movement(CashMovementType.PaidIn, 1000m));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turno abierto*");
    }

    [Fact]
    public async Task EmptyManagerRequiredFor_MakesExpenseFree()
    {
        var service = new CashShiftService(_db, _clock, new CashShiftSettings { ManagerRequiredFor = new List<string>() });
        await service.OpenShiftAsync("Caja", 100000m);

        var dto = await service.RecordMovementAsync(Movement(CashMovementType.Expense, 8000m, "Bolsas"));

        dto.ManagerOverrideId.Should().BeNull();
        (await service.GetReconciliationAsync()).Expenses.Should().Be(8000m);
    }

    [Fact]
    public async Task CustomManagerRequiredFor_CanRequirePaidIn()
    {
        var service = new CashShiftService(_db, _clock, new CashShiftSettings { ManagerRequiredFor = new List<string> { "paidin" } });
        await service.OpenShiftAsync("Caja", 100000m);

        var act = () => service.RecordMovementAsync(Movement(CashMovementType.PaidIn, 1000m));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*autorización*");
        (await service.RecordMovementAsync(Movement(CashMovementType.PaidOut, 1000m))).Amount.Should().Be(1000m, "PaidOut no longer listed");
    }
}
