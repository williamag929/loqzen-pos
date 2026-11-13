using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;

namespace Infrastructure.Data.Sync;

/// <summary>
/// Satellite → master HTTP client. Base URL = <see cref="TerminalSettings.MasterUrl"/>, optional Bearer =
/// <see cref="TerminalSettings.ApiKey"/>. Every call is bounded by <see cref="Timeout"/> through a linked
/// CancellationTokenSource (same pattern as HttpOutboxSink) so the shared HttpClient is never reconfigured.
/// Transport errors surface as HttpRequestException / TimeoutException; callers treat them as "master offline".
/// </summary>
public class MasterApiClient : IMasterApiClient
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;
    private readonly TerminalSettings _settings;

    public MasterApiClient(HttpClient http, TerminalSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<CatalogSnapshotDto> GetSnapshotAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/catalog/snapshot"));
        using var response = await SendAsync(request, ct);
        EnsureSuccess(response);
        return await ReadAsync<CatalogSnapshotDto>(response, ct)
            ?? throw new HttpRequestException("La caja principal devolvió un snapshot vacío.");
    }

    public async Task<TerminalDto> HeartbeatAsync(TerminalHeartbeatRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url("/api/terminals/heartbeat"))
        {
            Content = new StringContent(JsonSerializer.Serialize(request, SerializerOptions), Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(message, ct);
        EnsureSuccess(response);
        return await ReadAsync<TerminalDto>(response, ct)
            ?? throw new HttpRequestException("La caja principal no devolvió la caja registrada.");
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/health"));
        using var response = await SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string Url(string path)
    {
        if (string.IsNullOrWhiteSpace(_settings.MasterUrl))
            throw new InvalidOperationException("Terminal.MasterUrl no está configurada: la caja satélite no sabe dónde está la caja principal.");
        return _settings.MasterUrl.TrimEnd('/') + path;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            // Buffer the body before the linked token is disposed: the callers read it after this returns.
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"La caja principal no respondió en {Timeout.TotalSeconds:0} s.");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, ct);
    }
}
