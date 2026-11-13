using System.Net;
using System.Text.Json;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Application.Sync;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Outbox;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Outbox;

/// <summary>The envelope's <c>terminalCode</c>: present when an ITerminalContext is set, null otherwise; round-trips into SyncEnvelopeDto.</summary>
public class HttpOutboxSinkTerminalTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static OutboxMessage Message() => new()
    {
        EventType = "CashShiftOpened",
        AggregateType = "CashShift",
        AggregateId = Guid.NewGuid(),
        PayloadJson = "{\"shift\":{\"id\":\"11111111-1111-1111-1111-111111111111\",\"shiftNumber\":3}}",
        Status = SyncStatus.Pending,
        CreatedAt = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc)
    };

    private static async Task<JsonElement> SendAsync(ITerminalContext? ctx, OutboxMessage? msg = null)
    {
        var handler = new CaptureHandler();
        var sink = new HttpOutboxSink(new HttpClient(handler), new SyncSettings { Enabled = true, WebhookUrl = "http://master:5055/api/sync/inbox" }, ctx);
        await sink.DeliverAsync(msg ?? Message());
        using var doc = JsonDocument.Parse(handler.Body!);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task WithContext_EnvelopeCarriesTerminalCode()
    {
        var root = await SendAsync(Terminals.Satellite("CAJA-02"));
        root.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
    }

    [Fact]
    public async Task WithoutContext_TerminalCodeIsNullOrAbsent()
    {
        var root = await SendAsync(null);
        if (root.TryGetProperty("terminalCode", out var code))
            code.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task WithUnsetContext_TerminalCodeIsNullOrAbsent()
    {
        var root = await SendAsync(new TerminalContext());
        if (root.TryGetProperty("terminalCode", out var code))
            code.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Envelope_RoundTripsInto_SyncEnvelopeDto()
    {
        var msg = Message();
        var root = await SendAsync(Terminals.Satellite("caja-02"), msg);

        var dto = JsonSerializer.Deserialize<SyncEnvelopeDto>(root.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        dto.Id.Should().Be(msg.Id);
        dto.EventType.Should().Be("CashShiftOpened");
        dto.AggregateType.Should().Be("CashShift");
        dto.AggregateId.Should().Be(msg.AggregateId);
        dto.OccurredAt.Should().Be(msg.CreatedAt);
        dto.TerminalCode.Should().Be("caja-02", "the sink sends the context code as-is; the inbox normalizes");
        dto.Payload.ValueKind.Should().Be(JsonValueKind.Object);
        dto.Payload.GetProperty("shift").GetProperty("shiftNumber").GetInt32().Should().Be(3);
    }
}
