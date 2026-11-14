using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// Master ComandaService.CloseRemotelyAsync: a satellite invoiced the comanda on ITS database and reports the close.
/// No Order row is written here; the comanda is closed with PaidByTerminalCode / RemoteOrderNumber / RemoteOrderId.
/// </summary>
public class CloseRemotelyTests : SharedComandaTestBase
{
    [Fact]
    public async Task CloseRemotely_LockHolder_ClosesWithRemoteFieldsAndClearsLock()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        Clock.Advance(TimeSpan.FromSeconds(30));
        var orderId = Guid.NewGuid();
        var closedAt = Now.AddSeconds(-5);
        var req = RemoteClose(c.Id, SatelliteCode, orderId, "ORD-000123", PaymentMethod.Card, 29750m, "AUTH-9", closedAt);

        var dto = await Service.CloseRemotelyAsync(req);

        dto.Status.Should().Be(ComandaStatus.Closed);
        dto.PaidByTerminalCode.Should().Be(SatelliteCode);
        dto.OrderId.Should().BeNull("the invoice lives on CAJA-02");
        dto.OrderNumber.Should().Be("ORD-000123", "falls back to RemoteOrderNumber");
        dto.PaymentLockedByTerminalCode.Should().BeNull();
        dto.PaymentLockedAt.Should().BeNull();
        dto.ClosedAt.Should().Be(closedAt);

        var row = Reload(c.Id);
        row.Status.Should().Be(ComandaStatus.Closed);
        row.PaidByTerminalCode.Should().Be(SatelliteCode);
        row.RemoteOrderNumber.Should().Be("ORD-000123");
        row.RemoteOrderId.Should().Be(orderId);
        row.OrderId.Should().BeNull();
        row.PaymentLockedByTerminalCode.Should().BeNull();
        row.PaymentLockedAt.Should().BeNull();
        row.ClosedAt.Should().Be(closedAt);
        Db.Orders.Count().Should().Be(0, "no Order row on the master: it arrives through the sync inbox");
        Db.Payments.Count().Should().Be(0);
    }

    [Fact]
    public async Task CloseRemotely_NoLock_IsAllowed()
    {
        var c = await OpenWithItemsAsync();

        var dto = await Service.CloseRemotelyAsync(RemoteClose(c.Id));

        dto.Status.Should().Be(ComandaStatus.Closed);
        dto.PaidByTerminalCode.Should().Be(SatelliteCode);
    }

    [Fact]
    public async Task CloseRemotely_StaleLockOfAnother_IsAllowed()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);
        Clock.Advance(TimeSpan.FromMinutes(11));

        var dto = await Service.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode));

        dto.Status.Should().Be(ComandaStatus.Closed);
        dto.PaidByTerminalCode.Should().Be(SatelliteCode);
        dto.PaymentLockedByTerminalCode.Should().BeNull();
    }

    [Fact]
    public async Task CloseRemotely_LockedByAnother_Throws()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La mesa la está cobrando CAJA-03.");

        var row = Reload(c.Id);
        row.Status.Should().Be(ComandaStatus.ReadyToPay);
        row.PaidByTerminalCode.Should().BeNull();
        row.PaymentLockedByTerminalCode.Should().Be(OtherSatelliteCode);
    }

    [Fact]
    public async Task CloseRemotely_OpenComandaWithPendingItems_ConfirmsThemAndCreatesTickets()
    {
        var c = await OpenWithItemsAsync();
        Db.KitchenOrders.Count().Should().Be(0);
        Events.Clear();

        await Service.CloseRemotelyAsync(RemoteClose(c.Id));

        var row = Reload(c.Id);
        row.Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed && i.KitchenOrderId != null && i.ConfirmedAt == Now);
        Db.KitchenOrders.Count().Should().Be(2);
        Db.KitchenOrders.Should().OnlyContain(k => k.OrderNumber == $"CMD-{c.Number}" && k.TableNumber == "Mesa 1" && k.Status == KitchenOrderStatus.Pending);
        Db.OutboxMessages.Count(m => m.EventType == "KitchenOrderCreated").Should().Be(2);

        var created = Events.Where(e => e.Type == KitchenEventType.Created).ToList();
        created.Should().HaveCount(2);
        created.Select(e => e.Station).Should().BeEquivalentTo(new[] { "Cocina Caliente", "Barra" });
        Events.Last().Type.Should().Be(KitchenEventType.ComandaChanged, "tickets first, then the comanda-list refresh");
    }

    [Fact]
    public async Task CloseRemotely_CancelledItemsAreNotSentToKitchen()
    {
        var c = await OpenWithItemsAsync();
        var soda = c.Items.Single(i => i.ProductId == Soda.Id);
        await Service.CancelPendingItemAsync(c.Id, soda.Id);

        await Service.CloseRemotelyAsync(RemoteClose(c.Id));

        Db.KitchenOrders.Count().Should().Be(1);
        Db.KitchenOrders.Single().Station.Should().Be("Cocina Caliente");
        Reload(c.Id).Items.Single(i => i.Id == soda.Id).Status.Should().Be(ComandaItemStatus.Cancelled);
    }

    [Fact]
    public async Task CloseRemotely_SameTerminalAgain_IsIdempotent()
    {
        var c = await OpenWithItemsAsync();
        var orderId = Guid.NewGuid();
        await Service.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode, orderId, "ORD-1"));
        var ticketsBefore = Db.KitchenOrders.Count();
        Events.Clear();

        // Retry (HTTP timeout / Outbox redelivery): same terminal, possibly a different payload copy.
        var dto = await Service.CloseRemotelyAsync(RemoteClose(c.Id, "caja-02", orderId, "ORD-1"));

        dto.Status.Should().Be(ComandaStatus.Closed);
        dto.PaidByTerminalCode.Should().Be(SatelliteCode);
        dto.OrderNumber.Should().Be("ORD-1");
        Db.KitchenOrders.Count().Should().Be(ticketsBefore, "no new tickets");
        Events.Should().BeEmpty("nothing changed, nothing published");
        Reload(c.Id).RemoteOrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task CloseRemotely_AlreadyClosedByAnotherTerminal_Throws()
    {
        var c = await OpenWithItemsAsync();
        await Service.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode, null, "ORD-1"));

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id, OtherSatelliteCode, null, "ORD-2")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*CAJA-02*");

        var row = Reload(c.Id);
        row.PaidByTerminalCode.Should().Be(SatelliteCode);
        row.RemoteOrderNumber.Should().Be("ORD-1");
    }

    [Fact]
    public async Task CloseRemotely_AlreadyClosedLocally_Throws()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        (await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m)).Success.Should().BeTrue();

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La comanda ya fue cerrada por*");
    }

    [Fact]
    public async Task CloseRemotely_CancelledComanda_Throws()
    {
        var c = await OpenWithItemsAsync();
        var row = await Db.Comandas.SingleAsync(x => x.Id == c.Id);
        row.Status = ComandaStatus.Cancelled;
        await Db.SaveChangesAsync();

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La comanda está anulada.");
    }

    [Fact]
    public async Task CloseRemotely_UnknownComanda_Throws()
    {
        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(Guid.NewGuid())))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Comanda no encontrada.");
    }

    [Fact]
    public async Task CloseRemotely_ComandaWithoutBillableItems_Throws()
    {
        var c = await OpenWithItemsAsync();
        foreach (var i in c.Items) await Service.CancelPendingItemAsync(c.Id, i.Id);

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La comanda no tiene productos.");
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task CloseRemotely_EmptyOrderId_ThrowsArgumentException()
    {
        var c = await OpenWithItemsAsync();

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode, Guid.Empty)))
            .Should().ThrowAsync<ArgumentException>();
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CloseRemotely_BlankOrderNumber_ThrowsArgumentException(string number)
    {
        var c = await OpenWithItemsAsync();

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode, null, number)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CloseRemotely_BlankTerminalCode_ThrowsArgumentException()
    {
        var c = await OpenWithItemsAsync();

        await Service.Invoking(s => s.CloseRemotelyAsync(RemoteClose(c.Id, "")))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CloseRemotely_NullRequest_Throws()
    {
        await Service.Invoking(s => s.CloseRemotelyAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CloseRemotely_DefaultClosedAt_UsesClock()
    {
        var c = await OpenWithItemsAsync();

        var dto = await Service.CloseRemotelyAsync(RemoteClose(c.Id, closedAt: default(DateTime)));

        dto.ClosedAt.Should().Be(Now);
    }

    [Fact]
    public async Task CloseRemotely_UnspecifiedKindClosedAt_IsTreatedAsUtc()
    {
        var c = await OpenWithItemsAsync();
        var unspecified = new DateTime(2026, 11, 12, 11, 30, 0, DateTimeKind.Unspecified);

        await Service.CloseRemotelyAsync(RemoteClose(c.Id, closedAt: unspecified));

        Reload(c.Id).ClosedAt.Should().Be(new DateTime(2026, 11, 12, 11, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task CloseRemotely_TrimsAndTruncatesOrderNumber()
    {
        var c = await OpenWithItemsAsync();
        var longNumber = new string('X', 60);

        await Service.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode, null, "  " + longNumber + "  "));

        Reload(c.Id).RemoteOrderNumber.Should().Be(new string('X', 50));
    }

    [Fact]
    public async Task CloseRemotely_RemovesComandaFromActiveListAndFreesTable()
    {
        var table = new Core.Domain.Entities.DiningTable { Name = "Mesa 4", SortOrder = 4 };
        Db.DiningTables.Add(table);
        await Db.SaveChangesAsync();
        var c = await Service.OpenComandaAsync("Mesa 4", "Ana", table.Id);
        await Service.AddItemAsync(c.Id, Burger.Id);

        await Service.CloseRemotelyAsync(RemoteClose(c.Id));

        (await Service.GetActiveComandasAsync()).Should().NotContain(x => x.Id == c.Id);
        (await Service.GetTablesAsync()).Single(t => t.Id == table.Id).OpenComandaId.Should().BeNull();
        // The table can be opened again right away.
        var again = await Service.OpenComandaAsync("Mesa 4", "Ana", table.Id);
        again.Number.Should().Be(c.Number + 1);
    }

    // ── DTO mapping ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Dto_OrderNumber_PrefersLocalOrderOverRemote()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        var result = await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m);

        var dto = await Service.GetByIdAsync(c.Id);

        dto!.OrderId.Should().Be(result.OrderId);
        dto.OrderNumber.Should().Be(result.OrderNumber);
        dto.PaidByTerminalCode.Should().BeNull();
    }

    [Fact]
    public async Task Dto_OrderNumber_FallsBackToRemoteOrderNumber()
    {
        var c = await OpenWithItemsAsync();
        await Service.CloseRemotelyAsync(RemoteClose(c.Id, SatelliteCode, null, "ORD-777"));

        var dto = await Service.GetByIdAsync(c.Id);

        dto!.OrderId.Should().BeNull();
        dto.OrderNumber.Should().Be("ORD-777");
        dto.PaidByTerminalCode.Should().Be(SatelliteCode);
    }

    [Fact]
    public async Task Dto_MapsLockFieldsWhileLocked()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);

        var dto = await Service.GetByIdAsync(c.Id);

        dto!.PaymentLockedByTerminalCode.Should().Be(SatelliteCode);
        dto.PaymentLockedAt.Should().Be(Now);
        dto.PaidByTerminalCode.Should().BeNull();
        dto.OrderNumber.Should().BeNull();
    }
}
