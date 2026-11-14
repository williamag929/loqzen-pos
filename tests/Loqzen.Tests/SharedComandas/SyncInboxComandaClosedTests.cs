using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Services;
using Infrastructure.Data.Sync;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// Master SyncInboxService applying ComandaClosedRemotely envelopes (payload built by the real
/// OutboxPayloads.ComandaClosedRemotely). The satellite CAJA-02 invoiced a master comanda on its own database.
/// </summary>
public class SyncInboxComandaClosedTests : SharedComandaTestBase
{
    private readonly SyncInboxService _inbox;

    public SyncInboxComandaClosedTests()
    {
        Db.Terminals.Add(new Terminal { Id = Master.Id, Code = Master.Code, Name = Master.Name, Kind = TerminalKind.Master, IsActive = true });
        Db.SaveChanges();
        _inbox = new SyncInboxService(Db, Clock, Master); // no IComandaService: builds its own ComandaService (no bus)
    }

    private SyncEnvelopeDto Envelope(ComandaRemoteCloseRequest r, string sender = SatelliteCode, Guid? id = null, Guid? aggregateId = null) =>
        WireJson.Envelope(SyncEventTypes.ComandaClosedRemotely, aggregateId ?? r.ComandaId, sender, WireJson.ComandaClosedRemotelyPayload(r), Now, id);

    private SyncInboxReceipt Receipt(Guid messageId)
    {
        using var ctx = MasterDb.NewContext();
        return ctx.SyncInboxReceipts.Single(r => r.MessageId == messageId);
    }

    [Fact]
    public void Payload_HasTheDocumentedShape()
    {
        var r = RemoteClose(Guid.NewGuid(), SatelliteCode, Guid.NewGuid(), "ORD-1", PaymentMethod.Card, 29750m, "AUTH");
        using var doc = System.Text.Json.JsonDocument.Parse(WireJson.ComandaClosedRemotelyPayload(r));
        var root = doc.RootElement;

        var comanda = root.GetProperty("comanda");
        comanda.GetProperty("comandaId").GetGuid().Should().Be(r.ComandaId);
        comanda.GetProperty("terminalCode").GetString().Should().Be(SatelliteCode);
        comanda.GetProperty("orderId").GetGuid().Should().Be(r.OrderId);
        comanda.GetProperty("orderNumber").GetString().Should().Be("ORD-1");
        comanda.GetProperty("method").GetString().Should().Be("Card", "enums travel as names");
        comanda.GetProperty("total").GetDecimal().Should().Be(29750m);
        comanda.GetProperty("reference").GetString().Should().Be("AUTH");
        root.GetProperty("comandaId").GetGuid().Should().Be(r.ComandaId);
        root.GetProperty("terminalCode").GetString().Should().Be(SatelliteCode);
        root.GetProperty("orderId").GetGuid().Should().Be(r.OrderId);
        root.GetProperty("orderNumber").GetString().Should().Be("ORD-1");
        root.GetProperty("paymentMethod").GetString().Should().Be("Card");
        root.GetProperty("total").GetDecimal().Should().Be(29750m);
    }

    [Fact]
    public async Task Apply_ClosesComandaWithPaidByTerminalCode()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        var orderId = Guid.NewGuid();
        var env = Envelope(RemoteClose(c.Id, SatelliteCode, orderId, "ORD-000123"));

        var result = await _inbox.ApplyAsync(env);

        result.Outcome.Should().Be(SyncInboxOutcome.Applied, result.Error);
        result.IsAcknowledged.Should().BeTrue();
        var row = Reload(c.Id);
        row.Status.Should().Be(ComandaStatus.Closed);
        row.PaidByTerminalCode.Should().Be(SatelliteCode);
        row.RemoteOrderNumber.Should().Be("ORD-000123");
        row.RemoteOrderId.Should().Be(orderId);
        row.OrderId.Should().BeNull();
        row.PaymentLockedByTerminalCode.Should().BeNull();
        Db.Orders.Count().Should().Be(0, "the Order row arrives with the satellite's OrderCompleted");

        var receipt = Receipt(env.Id);
        receipt.Outcome.Should().Be(SyncInboxOutcome.Applied);
        receipt.TerminalCode.Should().Be(SatelliteCode);
        receipt.EventType.Should().Be(SyncEventTypes.ComandaClosedRemotely);
        receipt.AggregateId.Should().Be(c.Id);

        using var ctx = MasterDb.NewContext();
        var sender = ctx.Terminals.Single(t => t.Code == SatelliteCode);
        sender.Kind.Should().Be(TerminalKind.Satellite, "auto-registered sender");
        sender.LastSyncAt.Should().Be(Now);
    }

    [Fact]
    public async Task Apply_OpenComandaWithPendingItems_ConfirmsThemToKitchen()
    {
        var c = await OpenWithItemsAsync();

        var result = await _inbox.ApplyAsync(Envelope(RemoteClose(c.Id)));

        result.Outcome.Should().Be(SyncInboxOutcome.Applied, result.Error);
        Reload(c.Id).Items.Should().OnlyContain(i => i.Status == ComandaItemStatus.Confirmed);
        Db.KitchenOrders.Count().Should().Be(2);
    }

    [Fact]
    public async Task Apply_DuplicateMessageId_IsDuplicate()
    {
        var c = await OpenWithItemsAsync();
        var env = Envelope(RemoteClose(c.Id));
        (await _inbox.ApplyAsync(env)).Outcome.Should().Be(SyncInboxOutcome.Applied);
        var tickets = Db.KitchenOrders.Count();

        var again = await _inbox.ApplyAsync(env);

        again.Outcome.Should().Be(SyncInboxOutcome.Duplicate);
        again.IsAcknowledged.Should().BeTrue();
        Db.KitchenOrders.Count().Should().Be(tickets);
        Db.SyncInboxReceipts.Count().Should().Be(1);
    }

    [Fact]
    public async Task Apply_SameTerminalNewMessageId_IsAppliedIdempotently()
    {
        var c = await OpenWithItemsAsync();
        var r = RemoteClose(c.Id, SatelliteCode, Guid.NewGuid(), "ORD-1");
        (await _inbox.ApplyAsync(Envelope(r))).Outcome.Should().Be(SyncInboxOutcome.Applied);
        var tickets = Db.KitchenOrders.Count();

        var retry = await _inbox.ApplyAsync(Envelope(r, id: Guid.NewGuid()));

        retry.Outcome.Should().Be(SyncInboxOutcome.Applied, retry.Error);
        Db.KitchenOrders.Count().Should().Be(tickets, "no new tickets");
        Db.SyncInboxReceipts.Count().Should().Be(2);
        var row = Reload(c.Id);
        row.Status.Should().Be(ComandaStatus.Closed);
        row.PaidByTerminalCode.Should().Be(SatelliteCode);
        row.RemoteOrderNumber.Should().Be("ORD-1");
    }

    [Fact]
    public async Task Apply_PayloadTerminalDiffersFromSender_IsRejected()
    {
        var c = await OpenWithItemsAsync();
        var env = Envelope(RemoteClose(c.Id, OtherSatelliteCode), sender: SatelliteCode);

        var result = await _inbox.ApplyAsync(env);

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        result.IsAcknowledged.Should().BeFalse();
        result.Error.Should().Contain("CAJA-03").And.Contain("CAJA-02");
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open);
        Receipt(env.Id).Outcome.Should().Be(SyncInboxOutcome.Rejected);
    }

    [Fact]
    public async Task Apply_UnknownComanda_IsRejected()
    {
        var env = Envelope(RemoteClose(Guid.NewGuid()));

        var result = await _inbox.ApplyAsync(env);

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        result.Error.Should().Be("Comanda no encontrada.");
        Receipt(env.Id).Error.Should().Be("Comanda no encontrada.");
    }

    [Fact]
    public async Task Apply_ClosedByAnotherTerminal_IsRejected()
    {
        var c = await OpenWithItemsAsync();
        await Service.CloseRemotelyAsync(RemoteClose(c.Id, OtherSatelliteCode, null, "ORD-3"));

        var result = await _inbox.ApplyAsync(Envelope(RemoteClose(c.Id, SatelliteCode, null, "ORD-2")));

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        result.Error.Should().Contain("CAJA-03");
        var row = Reload(c.Id);
        row.PaidByTerminalCode.Should().Be(OtherSatelliteCode);
        row.RemoteOrderNumber.Should().Be("ORD-3");
    }

    [Fact]
    public async Task Apply_ClosedLocallyOnMaster_IsRejected()
    {
        var c = await OpenWithItemsAsync();
        var shift = await OpenShiftAsync();
        (await Service.CloseAndPayAsync(c.Id, shift, PaymentMethod.Cash, 100000m)).Success.Should().BeTrue();

        var result = await _inbox.ApplyAsync(Envelope(RemoteClose(c.Id)));

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        result.Error.Should().StartWith("La comanda ya fue cerrada por");
    }

    [Fact]
    public async Task Apply_LockedByAnotherRegister_IsRejected()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);

        var result = await _inbox.ApplyAsync(Envelope(RemoteClose(c.Id)));

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        result.Error.Should().Be("La mesa la está cobrando CAJA-03.");
        Reload(c.Id).Status.Should().Be(ComandaStatus.ReadyToPay);
    }

    [Fact]
    public async Task Apply_RejectedThenFixed_IsReEvaluated()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);
        var env = Envelope(RemoteClose(c.Id));
        (await _inbox.ApplyAsync(env)).Outcome.Should().Be(SyncInboxOutcome.Rejected);

        await Service.UnlockPaymentAsync(c.Id, OtherSatelliteCode);
        var retry = await _inbox.ApplyAsync(env);

        retry.Outcome.Should().Be(SyncInboxOutcome.Applied, retry.Error);
        Db.SyncInboxReceipts.Count().Should().Be(1, "the rejected receipt is updated in place");
        Receipt(env.Id).Outcome.Should().Be(SyncInboxOutcome.Applied);
    }

    [Fact]
    public async Task Apply_MessageFromThisMaster_IsRejected()
    {
        var c = await OpenWithItemsAsync();

        var result = await _inbox.ApplyAsync(Envelope(RemoteClose(c.Id, MasterCode), sender: MasterCode));

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task Apply_MissingComandaSection_IsRejected()
    {
        var c = await OpenWithItemsAsync();
        var env = WireJson.Envelope(SyncEventTypes.ComandaClosedRemotely, c.Id, SatelliteCode,
            WireJson.Serialize(new { comandaId = c.Id, terminalCode = SatelliteCode }), Now);

        var result = await _inbox.ApplyAsync(env);

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open);
    }

    [Fact]
    public async Task Apply_EmptyComandaIdFallsBackToAggregateId()
    {
        var c = await OpenWithItemsAsync();
        var env = Envelope(RemoteClose(Guid.Empty), aggregateId: c.Id);

        var result = await _inbox.ApplyAsync(env);

        result.Outcome.Should().Be(SyncInboxOutcome.Applied, result.Error);
        Reload(c.Id).Status.Should().Be(ComandaStatus.Closed);
    }

    [Fact]
    public async Task Apply_DefaultClosedAt_UsesEnvelopeOccurredAt()
    {
        var c = await OpenWithItemsAsync();
        var occurred = Now.AddMinutes(-7);
        var env = WireJson.Envelope(SyncEventTypes.ComandaClosedRemotely, c.Id, SatelliteCode,
            WireJson.ComandaClosedRemotelyPayload(RemoteClose(c.Id, closedAt: default(DateTime))), occurred);

        var result = await _inbox.ApplyAsync(env);

        result.Outcome.Should().Be(SyncInboxOutcome.Applied, result.Error);
        Reload(c.Id).ClosedAt.Should().Be(occurred);
    }

    [Fact]
    public async Task Apply_WithInjectedComandaService_UsesItAndPublishesComandaChanged()
    {
        var c = await OpenWithItemsAsync();
        var inbox = new SyncInboxService(Db, Clock, Master, Service); // Service has the recording bus
        Events.Clear();

        var result = await inbox.ApplyAsync(Envelope(RemoteClose(c.Id)));

        result.Outcome.Should().Be(SyncInboxOutcome.Applied, result.Error);
        Events.Count(e => e.Type == KitchenEventType.Created).Should().Be(2, "pending items went to the kitchen");
        Events.Last().Type.Should().Be(KitchenEventType.ComandaChanged);
        Events.Last().KitchenOrderId.Should().Be(c.Id);
        Reload(c.Id).PaidByTerminalCode.Should().Be(SatelliteCode);
    }

    [Fact]
    public async Task Apply_WithInjectedComandaService_RejectionsStillReturnRejected()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, OtherSatelliteCode);
        var inbox = new SyncInboxService(Db, Clock, Master, Service);

        var result = await inbox.ApplyAsync(Envelope(RemoteClose(c.Id)));

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        result.Error.Should().Contain("CAJA-03");
    }

    [Fact]
    public async Task Apply_WithoutInjectedComandaService_StillAppliesLockHeldBySender()
    {
        var c = await OpenWithItemsAsync();
        await Service.LockForPaymentAsync(c.Id, SatelliteCode);
        using var ctx = MasterDb.NewContext();
        var inbox = new SyncInboxService(ctx, Clock, Master);

        var result = await inbox.ApplyAsync(Envelope(RemoteClose(c.Id)));

        result.Outcome.Should().Be(SyncInboxOutcome.Applied, result.Error);
        Reload(c.Id).Status.Should().Be(ComandaStatus.Closed);
    }

    [Fact]
    public async Task Apply_OnASatelliteInbox_IsRejected()
    {
        var c = await OpenWithItemsAsync();
        var satInbox = new SyncInboxService(Db, Clock, Loqzen.Tests.Fixtures.Terminals.Satellite(OtherSatelliteCode));

        var result = await satInbox.ApplyAsync(Envelope(RemoteClose(c.Id)));

        result.Outcome.Should().Be(SyncInboxOutcome.Rejected);
        Reload(c.Id).Status.Should().Be(ComandaStatus.Open);
    }
}
