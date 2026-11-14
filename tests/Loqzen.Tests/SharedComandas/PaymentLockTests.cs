using Core.Application.DTOs;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// Master ComandaService (CAJA-01): LockForPaymentAsync / UnlockPaymentAsync and how the lock gates
/// CloseAndPayAsync and ReopenAsync. Lock TTL = ComandaSettings.PaymentLockMinutes (10).
/// </summary>
public class PaymentLockTests : SharedComandaTestBase
{
    // ── LockForPaymentAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task Lock_OpenComanda_BecomesReadyToPayWithLockFields()
    {
        var c = await OpenWithItemsAsync();

        var dto = await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        dto.Status.Should().Be(ComandaStatus.ReadyToPay);
        dto.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        dto.PaymentLockedAt.Should().Be(Now);
        dto.PaidByTerminalCode.Should().BeNull();

        var row = Reload(c.Id);
        row.Status.Should().Be(ComandaStatus.ReadyToPay);
        row.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        row.PaymentLockedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Lock_OpenComanda_ConfirmsPendingItemsAndCreatesKitchenTickets()
    {
        var c = await OpenWithItemsAsync();

        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        Reload(c.Id).Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed && i.KitchenOrderId != null);
        Db.KitchenOrders.Count().Should().Be(2, "one ticket per station (kitchen + bar)");
        Db.KitchenOrders.Select(k => k.Station).Should().BeEquivalentTo(new[] { "Cocina Caliente", "Barra" });
    }

    [Fact]
    public async Task Lock_ReadyToPayComanda_KeepsStatusAndLocks()
    {
        var c = await OpenWithItemsAsync();
        await Service.MarkReadyToPayAsync(c.Id);

        var dto = await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        dto.Status.Should().Be(ComandaStatus.ReadyToPay);
        dto.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        Db.KitchenOrders.Count().Should().Be(2, "no extra tickets: items were already confirmed");
    }

    [Fact]
    public async Task Lock_SameCodeAgain_RefreshesPaymentLockedAt()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        var first = Now;

        Clock.Advance(TimeSpan.FromMinutes(3));
        var dto = await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        dto.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        dto.PaymentLockedAt.Should().Be(first.AddMinutes(3));
        Reload(c.Id).PaymentLockedAt.Should().Be(first.AddMinutes(3));
    }

    [Fact]
    public async Task Lock_SameCodeDifferentCase_IsTheSameHolder()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, "caja-02");

        var dto = await Service.LockForPaymentAsync(c.Id, " CAJA-02 ");

        dto.PaymentLockedByTerminalCode.Should().Be(SatelliteCode, "codes are normalized (trim + upper)");
    }

    [Fact]
    public async Task Lock_OtherCodeWithinTtl_ThrowsWithHolderCode()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Clock.Advance(TimeSpan.FromMinutes(9));

        var act = () => Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("La mesa la está cobrando CAJA-02.");
        Reload(c.Id).PaymentLockedByTerminalCode.Should().Be(SatelliteCode, "the lock is untouched");
    }

    [Fact]
    public async Task Lock_OtherCodeOnMasterItself_ThrowsToo()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        var act = () => Service.LockForPaymentAsync(c.Id, MasterCode);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CAJA-02*");
    }

    [Fact]
    public async Task Lock_StaleLockPastTtl_CanBeTakenByAnother()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        var dto = await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);

        dto.PaymentLockedByTerminalCode.Should().Be(OtherSatelliteCode);
        dto.PaymentLockedAt.Should().Be(Now);
        Reload(c.Id).PaymentLockedByTerminalCode.Should().Be(OtherSatelliteCode);
    }

    [Fact]
    public async Task Lock_TtlHonoursSettings()
    {
        Settings.PaymentLockMinutes = 2;
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        Clock.Advance(TimeSpan.FromMinutes(1));
        await Service.Invoking(s => s.LockForPaymentAsync(c.Id, OtherSatelliteCode))
            .Should().ThrowAsync<InvalidOperationException>("1 min < 2 min TTL");

        Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var dto = await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);
        dto.PaymentLockedByTerminalCode.Should().Be(OtherSatelliteCode);
    }

    [Fact]
    public async Task Lock_ClosedComanda_Throws()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        (await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m)).Success.Should().BeTrue();

        await Service.Invoking(s => s.LockForPaymentAsync(c.Id, SatelliteCode))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La comanda ya está cerrada.");
    }

    [Fact]
    public async Task Lock_CancelledComanda_Throws()
    {
        var c = await OpenWithItemsAsync();
        var row = await Db.Comandas.SingleAsync(x => x.Id == c.Id);
        row.Status = ComandaStatus.Cancelled;
        await Db.SaveChangesAsync();

        await Service.Invoking(s => s.LockForPaymentAsync(c.Id, SatelliteCode))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La comanda ya está cerrada.");
    }

    [Fact]
    public async Task Lock_ComandaWithoutItems_Throws()
    {
        var c = await Service.OpenComandaAsync("Mesa 9", "Ana");

        await Service.Invoking(s => s.LockForPaymentAsync(c.Id, SatelliteCode))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La comanda no tiene productos.");
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task Lock_UnknownComanda_Throws()
    {
        await Service.Invoking(s => s.LockForPaymentAsync(Guid.NewGuid(), SatelliteCode))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Comanda no encontrada.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("X")]
    public async Task Lock_InvalidTerminalCode_ThrowsArgumentException(string code)
    {
        var c = await OpenWithItemsAsync();

        await Service.Invoking(s => s.LockForPaymentAsync(c.Id, code)).Should().ThrowAsync<ArgumentException>();
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open, "nothing was touched");
    }

    // ── UnlockPaymentAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task Unlock_ByHolder_ClearsLockKeepsReadyToPay()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        var dto = await Service.UnlockPaymentAsync(c.Id, SatelliteCode);

        dto.PaymentLockedByTerminalCode.Should().BeNull();
        dto.PaymentLockedAt.Should().BeNull();
        dto.Status.Should().Be(ComandaStatus.ReadyToPay, "unlock does not reopen");
        var row = Reload(c.Id);
        row.PaymentLockedByTerminalCode.Should().BeNull();
        row.PaymentLockedAt.Should().BeNull();
    }

    [Fact]
    public async Task Unlock_ByOther_IsNoOp()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        var lockedAt = Now;
        Events.Clear();

        var dto = await Service.UnlockPaymentAsync(c.Id, OtherSatelliteCode);

        dto.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        dto.PaymentLockedAt.Should().Be(lockedAt);
        Reload(c.Id).PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        Events.Should().BeEmpty("nothing changed, nothing published");
    }

    [Fact]
    public async Task Unlock_WhenNotLocked_IsNoOp()
    {
        var c = await OpenWithItemsAsync();
        Events.Clear();

        var dto = await Service.UnlockPaymentAsync(c.Id, SatelliteCode);

        dto.PaymentLockedByTerminalCode.Should().BeNull();
        dto.Status.Should().Be(ComandaStatus.Open);
        Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Unlock_ThenOtherCanLock()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        await Service.UnlockPaymentAsync(c.Id, SatelliteCode);

        var dto = await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);

        dto.PaymentLockedByTerminalCode.Should().Be(OtherSatelliteCode);
    }

    // ── CloseAndPayAsync (master) vs the lock ────────────────────────────────

    [Fact]
    public async Task CloseAndPay_LockedByAnotherRegister_Throws409Message()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        var act = () => Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("La mesa la está cobrando CAJA-02.");
        Db.Orders.Count().Should().Be(0);
        Reload(c.Id).Status.Should().Be(ComandaStatus.ReadyToPay);
    }

    [Fact]
    public async Task CloseAndPay_LockedByThisRegister_SucceedsAndClearsLock()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        await Service.LockForPaymentAsync(c.Id, MasterCode);

        var result = await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var row = Reload(c.Id);
        row.Status.Should().Be(ComandaStatus.Closed);
        row.OrderId.Should().Be(result.OrderId);
        row.PaymentLockedByTerminalCode.Should().BeNull();
        row.PaymentLockedAt.Should().BeNull();
        row.PaidByTerminalCode.Should().BeNull("paid on this register: the Order row is local");
    }

    [Fact]
    public async Task CloseAndPay_StaleLockFromAnotherRegister_Succeeds()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Clock.Advance(TimeSpan.FromMinutes(11));

        var result = await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        Reload(c.Id).PaymentLockedByTerminalCode.Should().BeNull();
    }

    [Fact]
    public async Task CloseAndPay_Unlocked_Succeeds()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();

        var result = await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Card, 0m, "AUTH-1", null);

        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    // ── ReopenAsync vs the lock ──────────────────────────────────────────────

    [Fact]
    public async Task Reopen_LockedByAnotherRegister_Throws()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        await Service.Invoking(s => s.ReopenAsync(c.Id))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La mesa la está cobrando CAJA-02.");
        Reload(c.Id).Status.Should().Be(ComandaStatus.ReadyToPay);
    }

    [Fact]
    public async Task Reopen_LockedByThisRegister_Succeeds()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, MasterCode);

        var dto = await Service.ReopenAsync(c.Id);

        dto.Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task Reopen_AfterStaleLock_Succeeds()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Clock.Advance(TimeSpan.FromMinutes(11));

        var dto = await Service.ReopenAsync(c.Id);

        dto.Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task Reopen_AfterUnlock_Succeeds()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        await Service.UnlockPaymentAsync(c.Id, SatelliteCode);

        var dto = await Service.ReopenAsync(c.Id);

        dto.Status.Should().Be(ComandaStatus.Open);
    }

    // ── lock survives a fresh read / shows in lists ───────────────────────────

    [Fact]
    public async Task Lock_IsVisibleThroughGetByIdAndActiveList()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        var byId = await Service.GetByIdAsync(c.Id);
        var active = await Service.GetActiveComandasAsync();

        byId!.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        byId.PaymentLockedAt.Should().Be(Now);
        active.Should().ContainSingle(x => x.Id == c.Id).Which.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
    }
}
