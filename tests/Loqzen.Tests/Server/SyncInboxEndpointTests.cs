using System.Net;
using System.Text;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// POST /api/sync/inbox (the master side of the satellites' Outbox): Applied / Duplicate / Ignored answer 200,
/// Rejected answers 422 with the reason, malformed or incomplete envelopes answer 400 with an ErrorDto.
/// </summary>
public class SyncInboxEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static readonly DateTime OccurredAt = new(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc);

    /// <summary>A contract-shaped OrderCompleted envelope as HttpOutboxSink would send it from CAJA-02.</summary>
    private static object OrderCompletedEnvelope(Guid? messageId = null, Guid? orderId = null, Guid? shiftId = null, string terminalCode = "CAJA-02", string orderNumber = "ORD-20261112130000-AB12")
    {
        var oid = orderId ?? Guid.NewGuid();
        var sid = shiftId ?? Guid.NewGuid();
        var satelliteTerminalId = Guid.NewGuid();
        return new
        {
            id = messageId ?? Guid.NewGuid(),
            eventType = "OrderCompleted",
            aggregateType = "Order",
            aggregateId = oid,
            occurredAt = OccurredAt,
            terminalCode,
            payload = new
            {
                order = new
                {
                    id = oid, orderNumber, status = "Completed",
                    subtotal = 10000m, taxTotal = 1900m, discountTotal = 0m, total = 11900m,
                    notes = (string?)null, customerId = (Guid?)null, cashShiftId = sid, shiftNumber = 7,
                    terminalId = satelliteTerminalId, terminalCode,
                    createdAt = OccurredAt.AddMinutes(-2), completedAt = OccurredAt,
                    items = new[]
                    {
                        new { id = Guid.NewGuid(), productId = Guid.NewGuid(), productName = "Gaseosa 400ml", unitPrice = 5000m, quantity = 2m, weightKg = (decimal?)null, taxRate = 0.19m, taxAmount = 1900m, subtotal = 10000m, total = 11900m }
                    },
                    payments = new[]
                    {
                        new { id = Guid.NewGuid(), method = "Cash", amount = 11900m, tenderedAmount = 12000m, changeAmount = 100m, reference = (string?)null, processedAt = OccurredAt }
                    }
                },
                // legacy flat keys travel too
                orderId = oid, orderNumber, total = 11900m, paymentMethod = "Cash", cashShiftId = sid, terminalCode, completedAt = OccurredAt
            }
        };
    }

    private Task<HttpResponseMessage> PostRaw(string json) =>
        _fx.Client.PostAsync("/api/sync/inbox", new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task ValidOrderCompleted_IsApplied_AndTheOrderLandsInTheMasterDb_StampedWithTheSender()
    {
        var orderId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var envelope = OrderCompletedEnvelope(orderId: orderId, shiftId: shiftId);

        var response = await _fx.PostJsonAsync("/api/sync/inbox", envelope);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["outcome"]!.GetValue<string>().Should().Be("Applied");
        body["error"].Should().BeNull();

        var order = await _fx.WithDbAsync(db => db.Orders.Include(o => o.Items).Include(o => o.Payments).SingleAsync(o => o.Id == orderId));
        order.Status.Should().Be(OrderStatus.Completed);
        order.Total.Should().Be(11900m);
        order.TerminalCode.Should().Be("CAJA-02");
        order.CashShiftId.Should().Be(shiftId);
        order.Items.Should().ContainSingle().Which.ProductName.Should().Be("Gaseosa 400ml");
        order.Payments.Should().ContainSingle().Which.Method.Should().Be(PaymentMethod.Cash);

        // The sender was auto-registered and the order's TerminalId is THIS database's row for CAJA-02.
        var sender = await _fx.WithDbAsync(db => db.Terminals.SingleAsync(t => t.Code == "CAJA-02"));
        sender.Kind.Should().Be(TerminalKind.Satellite);
        sender.LastSyncAt.Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
        order.TerminalId.Should().Be(sender.Id);

        // A placeholder shift was created so the FK holds until CashShiftOpened/Closed fills it.
        var shift = await _fx.WithDbAsync(db => db.CashShifts.SingleAsync(s => s.Id == shiftId));
        shift.TerminalCode.Should().Be("CAJA-02");
        shift.ShiftNumber.Should().Be(7, "taken from payload.order.shiftNumber");
        shift.Status.Should().Be(CashShiftStatus.Open);

        // Receipt row (idempotency ledger).
        var receipt = await _fx.WithDbAsync(db => db.SyncInboxReceipts.SingleAsync());
        receipt.Outcome.Should().Be(SyncInboxOutcome.Applied);
        receipt.TerminalCode.Should().Be("CAJA-02");
        receipt.EventType.Should().Be("OrderCompleted");
    }

    [Fact]
    public async Task SameEnvelopeTwice_SecondIsDuplicate_AndNothingIsDoubled()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var envelope = OrderCompletedEnvelope(messageId, orderId);

        (await _fx.PostJsonAsync("/api/sync/inbox", envelope)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await _fx.PostJsonAsync("/api/sync/inbox", envelope);

        second.StatusCode.Should().Be(HttpStatusCode.OK, "the satellite must mark the message Synced");
        (await _fx.ReadJsonAsync(second))["outcome"]!.GetValue<string>().Should().Be("Duplicate");
        (await _fx.WithDbAsync(db => db.Orders.CountAsync(o => o.Id == orderId))).Should().Be(1);
        (await _fx.WithDbAsync(db => db.SyncInboxReceipts.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task KitchenOrderCreated_IsIgnored_ButAcknowledged()
    {
        var envelope = new
        {
            id = Guid.NewGuid(), eventType = "KitchenOrderCreated", aggregateType = "KitchenOrder", aggregateId = Guid.NewGuid(),
            occurredAt = OccurredAt, terminalCode = "CAJA-02",
            payload = new { kitchenOrderId = Guid.NewGuid(), comandaId = Guid.NewGuid(), table = "Mesa 3", items = 2 }
        };

        var response = await _fx.PostJsonAsync("/api/sync/inbox", envelope);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["outcome"]!.GetValue<string>().Should().Be("Ignored");
        (await _fx.WithDbAsync(db => db.KitchenOrders.CountAsync(k => k.Id == envelope.aggregateId))).Should().Be(0);
        var receipt = await _fx.WithDbAsync(db => db.SyncInboxReceipts.SingleAsync());
        receipt.Outcome.Should().Be(SyncInboxOutcome.Ignored);
    }

    [Fact]
    public async Task EnvelopeFromOwnCode_IsRejected_With422AndError()
    {
        var envelope = OrderCompletedEnvelope(terminalCode: "CAJA-01");

        var response = await _fx.PostJsonAsync("/api/sync/inbox", envelope);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await _fx.ReadJsonAsync(response);
        body["outcome"]!.GetValue<string>().Should().Be("Rejected");
        body["error"]!.GetValue<string>().Should().Contain("CAJA-01");
        (await _fx.WithDbAsync(db => db.Orders.CountAsync(o => o.TerminalCode == "CAJA-01" && o.Status == OrderStatus.Completed))).Should().Be(0);
        (await _fx.WithDbAsync(db => db.SyncInboxReceipts.SingleAsync())).Outcome.Should().Be(SyncInboxOutcome.Rejected);
    }

    [Fact]
    public async Task EnvelopeWithoutTerminalCode_IsRejected_With422()
    {
        var response = await _fx.PostJsonAsync("/api/sync/inbox", new
        {
            id = Guid.NewGuid(), eventType = "OrderCompleted", aggregateType = "Order", aggregateId = Guid.NewGuid(),
            occurredAt = OccurredAt, terminalCode = (string?)null, payload = new { order = new { id = Guid.NewGuid() } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("terminalCode");
    }

    [Fact]
    public async Task LegacyFlatOrderPayload_IsRejected_WithClearError()
    {
        var response = await _fx.PostJsonAsync("/api/sync/inbox", new
        {
            id = Guid.NewGuid(), eventType = "OrderCompleted", aggregateType = "Order", aggregateId = Guid.NewGuid(),
            occurredAt = OccurredAt, terminalCode = "CAJA-02",
            payload = new { orderId = Guid.NewGuid(), orderNumber = "ORD-1", total = 100m, paymentMethod = "Cash" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await _fx.ReadJsonAsync(response);
        body["outcome"]!.GetValue<string>().Should().Be("Rejected");
        body["error"]!.GetValue<string>().Should().Contain("'order'");
    }

    [Fact]
    public async Task MalformedJson_Returns400_WithErrorDto()
    {
        var response = await PostRaw("{ \"id\": \"not-closed\" ");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        (await _fx.WithDbAsync(db => db.SyncInboxReceipts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task MissingId_Returns400()
    {
        var response = await _fx.PostJsonAsync("/api/sync/inbox", new
        {
            eventType = "OrderCompleted", aggregateType = "Order", aggregateId = Guid.NewGuid(),
            occurredAt = OccurredAt, terminalCode = "CAJA-02", payload = new { }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("id");
    }

    [Fact]
    public async Task EmptyGuidId_Returns400()
    {
        var response = await _fx.PostJsonAsync("/api/sync/inbox", new
        {
            id = Guid.Empty, eventType = "OrderCompleted", aggregateType = "Order", aggregateId = Guid.NewGuid(),
            occurredAt = OccurredAt, terminalCode = "CAJA-02", payload = new { }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MissingEventType_Returns400()
    {
        var response = await _fx.PostJsonAsync("/api/sync/inbox", new
        {
            id = Guid.NewGuid(), aggregateType = "Order", aggregateId = Guid.NewGuid(),
            occurredAt = OccurredAt, terminalCode = "CAJA-02", payload = new { }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmptyBody_Returns400()
    {
        var response = await PostRaw("");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RejectedEnvelope_IsReEvaluatedOnRetry_AndAppliedOnceFixed()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        // Broken first attempt: no items array.
        var broken = new
        {
            id = messageId, eventType = "OrderCompleted", aggregateType = "Order", aggregateId = orderId,
            occurredAt = OccurredAt, terminalCode = "CAJA-02",
            payload = new { order = new { id = orderId, orderNumber = "ORD-X", payments = Array.Empty<object>() } }
        };
        var first = await _fx.PostJsonAsync("/api/sync/inbox", broken);
        first.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var fixedResponse = await _fx.PostJsonAsync("/api/sync/inbox", OrderCompletedEnvelope(messageId, orderId));

        fixedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(fixedResponse))["outcome"]!.GetValue<string>().Should().Be("Applied");
        (await _fx.WithDbAsync(db => db.Orders.CountAsync(o => o.Id == orderId))).Should().Be(1);
        var receipt = await _fx.WithDbAsync(db => db.SyncInboxReceipts.SingleAsync(r => r.MessageId == messageId));
        receipt.Outcome.Should().Be(SyncInboxOutcome.Applied);
        receipt.Error.Should().BeNull();
    }
}
