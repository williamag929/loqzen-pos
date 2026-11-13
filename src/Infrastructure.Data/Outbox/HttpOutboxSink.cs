using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.Sync;
using Core.Domain.Entities;

namespace Infrastructure.Data.Outbox;

/// <summary>
/// Delivers one Outbox message as a JSON envelope via HTTP POST to <see cref="SyncSettings.WebhookUrl"/>:
/// <c>{ id, eventType, aggregateType, aggregateId, occurredAt, payload }</c> where <c>payload</c> is the stored
/// PayloadJson embedded as a nested JSON value (not a string). Sends <c>Authorization: Bearer &lt;ApiKey&gt;</c>
/// when configured. The per-request timeout comes from <see cref="SyncSettings.TimeoutSeconds"/> through a
/// linked CancellationTokenSource, so the shared <see cref="HttpClient"/> is never reconfigured.
/// Any non-2xx response or transport error throws, which the dispatcher records as a failed attempt.
/// </summary>
public class HttpOutboxSink : IOutboxSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SyncSettings _settings;

    public HttpOutboxSink(HttpClient http, SyncSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task DeliverAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(_settings.WebhookUrl))
            throw new InvalidOperationException("Sync.WebhookUrl is not configured.");

        var body = JsonSerializer.Serialize(BuildEnvelope(message), SerializerOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.WebhookUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout fired (not the caller's token): report it as a plain delivery failure.
            throw new TimeoutException($"Webhook did not answer within {timeout.TotalSeconds:0} s.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    /// <summary>
    /// PayloadJson is embedded as a nested JSON value. If it is not valid JSON (should not happen: every
    /// writer uses JsonSerializer) it is sent as a plain string so a corrupt row does not block the queue forever.
    /// </summary>
    private static object BuildEnvelope(OutboxMessage m)
    {
        object payload;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(m.PayloadJson) ? "null" : m.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            payload = m.PayloadJson;
        }

        return new
        {
            id = m.Id,
            eventType = m.EventType,
            aggregateType = m.AggregateType,
            aggregateId = m.AggregateId,
            occurredAt = m.CreatedAt,
            payload
        };
    }
}
