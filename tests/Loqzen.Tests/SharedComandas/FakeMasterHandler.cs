using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// In-test master: an HttpMessageHandler that records every request (method, path+query, body, Authorization) and
/// answers from a routing table (<see cref="On"/>) or a default (<see cref="Default"/>). Route keys are
/// "METHOD /path" (exact, path only, no query).
/// </summary>
public sealed class FakeMasterHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, string PathAndQuery, string Path, string? Body, AuthenticationHeaderValue? Authorization);

    private readonly Dictionary<string, Func<Recorded, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<Recorded> Requests { get; } = new();

    /// <summary>Response for unrouted requests (default: 404 { error }).</summary>
    public Func<Recorded, HttpResponseMessage> Default { get; set; } = r => Error(HttpStatusCode.NotFound, $"Sin ruta: {r.Method} {r.Path}");

    public FakeMasterHandler On(HttpMethod method, string path, Func<Recorded, HttpResponseMessage> respond)
    {
        _routes[$"{method} {path}"] = respond;
        return this;
    }

    public FakeMasterHandler On(HttpMethod method, string path, object jsonBody, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, path, _ => Json(jsonBody, status));

    public FakeMasterHandler OnError(HttpMethod method, string path, HttpStatusCode status, string? message) =>
        On(method, path, _ => Error(status, message));

    public FakeMasterHandler OnThrow(HttpMethod method, string path, Exception ex) =>
        On(method, path, _ => throw ex);

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(WireJson.Serialize(body), Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Raw(string text, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, mediaType)
    };

    /// <summary>Server-style error body <c>{ "error": "…" }</c>; null → empty body (reason phrase fallback).</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string? message) =>
        message == null ? new HttpResponseMessage(status) : Json(new { error = message }, status);

    public Recorded Single(HttpMethod method, string path) => Requests.Single(r => r.Method == method && r.Path == path);

    public bool Any(string pathSuffix) => Requests.Any(r => r.Path.EndsWith(pathSuffix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        var uri = request.RequestUri!;
        var rec = new Recorded(request.Method, uri.PathAndQuery, uri.AbsolutePath, body, request.Headers.Authorization);
        Requests.Add(rec);
        var key = $"{request.Method} {uri.AbsolutePath}";
        return _routes.TryGetValue(key, out var respond) ? respond(rec) : Default(rec);
    }
}
