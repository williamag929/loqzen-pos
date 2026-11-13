using System.Net;
using System.Text.Json;
using Core.Application.Sync;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Outbox;

namespace Loqzen.Tests.Outbox;

/// <summary>HTTP delivery of one Outbox message: envelope shape, auth header, error and timeout mapping.</summary>
public class HttpOutboxSinkTests
{
    private static readonly DateTime CreatedAt = new(2026, 11, 12, 12, 34, 56, DateTimeKind.Utc);

    private static OutboxMessage Message(string payloadJson = "{\"orderId\":\"abc\",\"total\":123.5}") => new()
    {
        EventType = "OrderCompleted",
        AggregateType = "Order",
        AggregateId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        PayloadJson = payloadJson,
        Status = SyncStatus.Pending,
        CreatedAt = CreatedAt
    };

    /// <summary>Captures the request (method, URL, auth header, body) and answers with a fixed status.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public string? BearerToken { get; private set; }

        public StubHandler(HttpStatusCode status) : this((_, _) => Task.FromResult(new HttpResponseMessage(status))) { }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            BearerToken = request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null;
            return await _respond(request, ct);
        }
    }

    private static (HttpOutboxSink sink, StubHandler handler) Build(StubHandler handler, SyncSettings? settings = null)
    {
        settings ??= new SyncSettings { Enabled = true, WebhookUrl = "http://erp.local/webhook" };
        return (new HttpOutboxSink(new HttpClient(handler), settings), handler);
    }

    [Fact]
    public async Task Success_PostsEnvelope_WithNestedPayload_AndOccurredAtFromCreatedAt()
    {
        var (sink, handler) = Build(new StubHandler(HttpStatusCode.OK));
        var msg = Message();

        await sink.DeliverAsync(msg);

        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().Should().Be("http://erp.local/webhook");
        handler.Request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        root.GetProperty("id").GetGuid().Should().Be(msg.Id);
        root.GetProperty("eventType").GetString().Should().Be("OrderCompleted");
        root.GetProperty("aggregateType").GetString().Should().Be("Order");
        root.GetProperty("aggregateId").GetGuid().Should().Be(msg.AggregateId);
        root.GetProperty("occurredAt").GetDateTime().Should().Be(CreatedAt);

        var payload = root.GetProperty("payload");
        payload.ValueKind.Should().Be(JsonValueKind.Object, "PayloadJson is embedded, not stringified");
        payload.GetProperty("orderId").GetString().Should().Be("abc");
        payload.GetProperty("total").GetDecimal().Should().Be(123.5m);
    }

    [Fact]
    public async Task Success_Accepts202()
    {
        var (sink, _) = Build(new StubHandler(HttpStatusCode.Accepted));
        var act = () => sink.DeliverAsync(Message());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApiKey_SendsBearerHeader()
    {
        var settings = new SyncSettings { Enabled = true, WebhookUrl = "http://erp.local/webhook", ApiKey = "secreto-123" };
        var (sink, handler) = Build(new StubHandler(HttpStatusCode.OK), settings);

        await sink.DeliverAsync(Message());

        handler.BearerToken.Should().Be("secreto-123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoApiKey_SendsNoAuthorizationHeader(string? apiKey)
    {
        var settings = new SyncSettings { Enabled = true, WebhookUrl = "http://erp.local/webhook", ApiKey = apiKey };
        var (sink, handler) = Build(new StubHandler(HttpStatusCode.OK), settings);

        await sink.DeliverAsync(Message());

        handler.Request!.Headers.Authorization.Should().BeNull();
        handler.BearerToken.Should().BeNull();
    }

    [Fact]
    public async Task ServerError_ThrowsHttpRequestException_WithStatusAndReason()
    {
        var (sink, _) = Build(new StubHandler(HttpStatusCode.InternalServerError));

        var act = () => sink.DeliverAsync(Message());

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("HTTP 500 Internal Server Error");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "HTTP 400*")]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401*")]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404*")]
    public async Task Non2xx_Throws(HttpStatusCode status, string expectedMessage)
    {
        var (sink, _) = Build(new StubHandler(status));
        var act = () => sink.DeliverAsync(Message());
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage(expectedMessage);
    }

    [Fact]
    public async Task Timeout_ThrowsTimeoutException_NotOperationCanceled()
    {
        // The handler never answers; it only completes when the sink's own timeout token fires.
        var handler = new StubHandler((_, ct) =>
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        });
        var settings = new SyncSettings { Enabled = true, WebhookUrl = "http://erp.local/webhook", TimeoutSeconds = 1 };
        var (sink, _) = Build(handler, settings);

        var act = () => sink.DeliverAsync(Message());

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*1 s*");
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceled()
    {
        var handler = new StubHandler((_, ct) =>
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        });
        var (sink, _) = Build(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sink.DeliverAsync(Message(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the caller's token is not a timeout");
    }

    [Fact]
    public async Task InvalidPayloadJson_IsSentAsString()
    {
        var (sink, handler) = Build(new StubHandler(HttpStatusCode.OK));

        await sink.DeliverAsync(Message(payloadJson: "{not json"));

        using var doc = JsonDocument.Parse(handler.Body!);
        var payload = doc.RootElement.GetProperty("payload");
        payload.ValueKind.Should().Be(JsonValueKind.String);
        payload.GetString().Should().Be("{not json");
    }

    [Fact]
    public async Task EmptyPayloadJson_IsSentAsNull()
    {
        var (sink, handler) = Build(new StubHandler(HttpStatusCode.OK));

        await sink.DeliverAsync(Message(payloadJson: ""));

        using var doc = JsonDocument.Parse(handler.Body!);
        doc.RootElement.GetProperty("payload").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task MissingWebhookUrl_Throws_WithoutCallingTheServer()
    {
        var settings = new SyncSettings { Enabled = true, WebhookUrl = "  " };
        var (sink, handler) = Build(new StubHandler(HttpStatusCode.OK), settings);

        var act = () => sink.DeliverAsync(Message());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*WebhookUrl*");
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task NullMessage_Throws()
    {
        var (sink, _) = Build(new StubHandler(HttpStatusCode.OK));
        var act = () => sink.DeliverAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
