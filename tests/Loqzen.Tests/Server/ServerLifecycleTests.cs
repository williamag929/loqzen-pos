using System.Globalization;
using System.Net;
using FluentAssertions;

namespace Loqzen.Tests.Server;

/// <summary>Start/stop semantics of the embedded Kestrel host and the /health probe.</summary>
public class ServerLifecycleTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    [Fact]
    public void PortZero_BindsEphemeralPort_AndReportsRunning()
    {
        _fx.Server.Port.Should().BeGreaterThan(0);
        _fx.Server.IsRunning.Should().BeTrue();
        _fx.Server.ConnectedClients.Should().Be(0);
        _fx.Server.KdsUrl.Should().EndWith($":{_fx.Server.Port}/kds").And.StartWith("http://");
    }

    [Fact]
    public async Task Health_ReturnsOk_WithClockTime_AndZeroClients()
    {
        var response = await _fx.Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await _fx.ReadJsonAsync(response);
        body["status"]!.GetValue<string>().Should().Be("ok");
        body["node"]!.GetValue<string>().Should().Be("loqzen-pos");
        body["version"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["connectedClients"]!.GetValue<int>().Should().Be(0);

        var utcNow = DateTime.Parse(body["utcNow"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        utcNow.Should().BeCloseTo(_fx.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Health_FollowsTheInjectedClock()
    {
        _fx.Clock.Advance(TimeSpan.FromHours(3));

        var body = await _fx.GetJsonAsync("/health");

        var utcNow = DateTime.Parse(body["utcNow"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        utcNow.Should().BeCloseTo(_fx.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RootAndKds_ServeTheEmbeddedPage()
    {
        foreach (var path in new[] { "/", "/kds" })
        {
            var response = await _fx.Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/html", path);
            (await response.Content.ReadAsStringAsync()).Should().Contain("Loqzen KDS", path);
        }
    }

    [Fact]
    public async Task WsRoute_WithoutUpgrade_Returns400()
    {
        var response = await _fx.Client.GetAsync("/ws/kds");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stop_IsIdempotent_AndClosesTheListener()
    {
        var port = _fx.Server.Port;

        await _fx.Server.StopAsync();
        _fx.Server.IsRunning.Should().BeFalse();

        await _fx.Server.StopAsync(); // second stop must be a no-op, not a throw
        _fx.Server.IsRunning.Should().BeFalse();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var act = () => client.GetAsync($"http://127.0.0.1:{port}/health");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Start_AfterStop_IsIdempotentWhileRunning_AndRestartsAfterStop()
    {
        // StartAsync while running: no-op, same port.
        var port = _fx.Server.Port;
        await _fx.Server.StartAsync();
        _fx.Server.Port.Should().Be(port);
        _fx.Server.IsRunning.Should().BeTrue();

        // Stop then start again: a new ephemeral port and a healthy listener.
        await _fx.Server.StopAsync();
        await _fx.Server.StartAsync();
        _fx.Server.IsRunning.Should().BeTrue();
        _fx.Server.Port.Should().BeGreaterThan(0);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await client.GetAsync($"http://127.0.0.1:{_fx.Server.Port}/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
