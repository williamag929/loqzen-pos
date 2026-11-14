using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;

namespace Infrastructure.Data.Sync;

/// <summary>
/// Satellite → master request plumbing shared by <see cref="RemoteComandaService"/> and <see cref="RemoteKitchenService"/>.
/// Base URL = <see cref="TerminalSettings.MasterUrl"/>, optional Bearer = <see cref="TerminalSettings.ApiKey"/>, JSON web
/// defaults with enums as names, <see cref="MasterApiClient.Timeout"/> per call through a linked CancellationTokenSource.
/// Error mapping mirrors the server's EndpointSupport.MapError so the satellite UI sees the SAME exceptions the local
/// services would throw: 400 → ArgumentException, 404 → KeyNotFoundException, 409 → InvalidOperationException (message =
/// the server's <c>{ error }</c>); 401 → InvalidOperationException (wrong ApiKey); any transport failure or timeout →
/// HttpRequestException("Sin conexión con la caja principal: …").
/// </summary>
internal sealed class MasterHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public const string OfflinePrefix = "Sin conexión con la caja principal";

    private readonly HttpClient _http;
    private readonly TerminalSettings _settings;

    public MasterHttp(HttpClient http, TerminalSettings settings)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public TerminalSettings Settings => _settings;

    /// <summary>Absolute URL for <paramref name="path"/> ("/api/…") under MasterUrl.</summary>
    public string Url(string path)
    {
        if (string.IsNullOrWhiteSpace(_settings.MasterUrl))
            throw new InvalidOperationException("Terminal.MasterUrl no está configurada: la caja satélite no sabe dónde está la caja principal.");
        return _settings.MasterUrl.TrimEnd('/') + path;
    }

    public Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct) => SendAsync<T>(HttpMethod.Post, path, body, ct);

    /// <summary>Sends and deserializes a 2xx body; maps every other outcome to the exceptions documented on the class.</summary>
    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct)
                   ?? throw new HttpRequestException($"{OfflinePrefix}: respuesta vacía de {path}.");
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"{OfflinePrefix}: respuesta inválida de {path} ({ex.Message}).", ex);
        }
    }

    /// <summary>Sends and returns the successful response (caller disposes). Same error mapping as <see cref="SendAsync{T}"/>.</summary>
    public async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(path));
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(MasterApiClient.Timeout);

        HttpResponseMessage response;
        try
        {
            // Buffer the body before the linked token is disposed: callers read it after this returns.
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"{OfflinePrefix}: no respondió en {MasterApiClient.Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"{OfflinePrefix}: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode) return response;

        string message;
        try
        {
            message = await ReadErrorAsync(response, ct);
        }
        finally
        {
            response.Dispose();
        }
        throw MapStatus(response.StatusCode, message);
    }

    private static Exception MapStatus(HttpStatusCode status, string message) => status switch
    {
        HttpStatusCode.BadRequest => new ArgumentException(message),
        HttpStatusCode.NotFound => new KeyNotFoundException(message),
        HttpStatusCode.Conflict => new InvalidOperationException(message),
        HttpStatusCode.Unauthorized => new InvalidOperationException("La caja principal rechazó la clave de esta caja (Terminal.ApiKey)."),
        _ => new HttpRequestException($"{OfflinePrefix}: HTTP {(int)status} {message}".TrimEnd(), null, status)
    };

    /// <summary>The server answers <c>{ "error": "…" }</c>; anything else falls back to the reason phrase.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? $"HTTP {(int)response.StatusCode}" : response.ReasonPhrase!;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(err.GetString()))
                return err.GetString()!;
            return fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
