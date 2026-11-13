using System.Net.Http.Json;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Core.Application.DTOs;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Server;

/// <summary>
/// Master side of the terminal registry over real HTTP: /health exposes this register, /api/terminals lists the
/// known registers, /api/terminals/heartbeat upserts a satellite (409 for own code / Master kind, 400 for no code).
/// </summary>
public class TerminalEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static DateTime ParseUtc(JsonNode? node) =>
        DateTime.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task Health_ReportsThisTerminal_AsMasterCaja01()
    {
        var body = await _fx.GetJsonAsync("/health");

        var terminal = body["terminal"]!.AsObject();
        terminal["code"]!.GetValue<string>().Should().Be("CAJA-01");
        terminal["name"]!.GetValue<string>().Should().Be(ServerFixture.MasterName);
        terminal["kind"]!.GetValue<string>().Should().Be("Master");
        terminal.Select(kv => kv.Key).Should().OnlyContain(k => char.IsLower(k[0]));
    }

    [Fact]
    public async Task GetTerminals_ListsTheMasterOnly_OnAFreshInstall()
    {
        var response = await _fx.Client.GetAsync("/api/terminals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = (await _fx.ReadJsonAsync(response)).AsArray();
        list.Should().HaveCount(1);
        var master = list[0]!;
        master["code"]!.GetValue<string>().Should().Be("CAJA-01");
        master["kind"]!.GetValue<string>().Should().Be("Master");
        master["isActive"]!.GetValue<bool>().Should().BeTrue();
        master["id"]!.GetValue<Guid>().Should().Be(_fx.Terminal.Id);
        master["lastSeenAt"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace("EnsureRegisteredAsync stamps LastSeenAt");
        master["openShiftNumber"].Should().BeNull("no shift is open yet");
        master.AsObject().Select(kv => kv.Key).Should().OnlyContain(k => char.IsLower(k[0]));
    }

    [Fact]
    public async Task Heartbeat_RegistersSatellite_WithLastSeenAndLoopbackAddress()
    {
        var response = await _fx.PostJsonAsync("/api/terminals/heartbeat",
            new TerminalHeartbeatRequest("caja-02", "Caja 2", TerminalKind.Satellite, "1.2.3"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await _fx.ReadJsonAsync(response);
        dto["code"]!.GetValue<string>().Should().Be("CAJA-02", "codes are normalized (trim + upper)");
        dto["name"]!.GetValue<string>().Should().Be("Caja 2");
        dto["kind"]!.GetValue<string>().Should().Be("Satellite");
        dto["isActive"]!.GetValue<bool>().Should().BeTrue();
        dto["appVersion"]!.GetValue<string>().Should().Be("1.2.3");
        ParseUtc(dto["lastSeenAt"]).Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
        var address = dto["lastAddress"]!.GetValue<string>();
        IPAddress.TryParse(address, out var ip).Should().BeTrue($"lastAddress should be an IP, got '{address}'");
        (IPAddress.IsLoopback(ip!) || ip!.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())).Should().BeTrue();

        // The registry now lists both: master first, then by code.
        var list = (await _fx.GetJsonAsync("/api/terminals")).AsArray();
        list.Select(t => t!["code"]!.GetValue<string>()).Should().Equal("CAJA-01", "CAJA-02");

        // Second heartbeat updates the same row (upsert by code).
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = await _fx.PostJsonAsync("/api/terminals/heartbeat",
            new TerminalHeartbeatRequest("CAJA-02", "Caja 2 renombrada", TerminalKind.Satellite, "1.2.4"));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await _fx.ReadJsonAsync(again);
        updated["id"]!.GetValue<Guid>().Should().Be(dto["id"]!.GetValue<Guid>());
        updated["name"]!.GetValue<string>().Should().Be("Caja 2 renombrada");
        ParseUtc(updated["lastSeenAt"]).Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
        (await _fx.GetJsonAsync("/api/terminals")).AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Heartbeat_WithOwnCode_Returns409()
    {
        var response = await _fx.PostJsonAsync("/api/terminals/heartbeat",
            new TerminalHeartbeatRequest("CAJA-01", "Impostor", TerminalKind.Satellite));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("CAJA-01");
        (await _fx.GetJsonAsync("/api/terminals")).AsArray().Should().HaveCount(1, "nothing was written");
    }

    [Fact]
    public async Task Heartbeat_ClaimingMasterKind_Returns409()
    {
        var response = await _fx.PostJsonAsync("/api/terminals/heartbeat",
            new TerminalHeartbeatRequest("CAJA-03", "Otra principal", TerminalKind.Master));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Heartbeat_WithEmptyCode_Returns400(string code)
    {
        var response = await _fx.PostJsonAsync("/api/terminals/heartbeat",
            new { code, name = "Sin código", kind = "Satellite" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Heartbeat_WithOneCharCode_Returns400_FromNormalizeCode()
    {
        var response = await _fx.PostJsonAsync("/api/terminals/heartbeat",
            new TerminalHeartbeatRequest("X", "Corta", TerminalKind.Satellite));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

/// <summary>
/// With TerminalSettings.ApiKey configured the master/satellite routes demand <c>Authorization: Bearer</c>;
/// the comandera/KDS routes stay open.
/// </summary>
public class TerminalApiKeyTests : IAsyncLifetime
{
    private const string ApiKey = "s3cret-lan-key";
    private readonly ServerFixture _fx = new(ApiKey);

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static object ValidIgnoredEnvelope() => new
    {
        id = Guid.NewGuid(), eventType = "KitchenOrderCreated", aggregateType = "KitchenOrder", aggregateId = Guid.NewGuid(),
        occurredAt = DateTime.UtcNow, terminalCode = "CAJA-02", payload = new { kitchenOrderId = Guid.NewGuid() }
    };

    private static Task<HttpResponseMessage> Hit(HttpClient client, string method, string path) =>
        method == "POST"
            ? client.PostAsJsonAsync(path, ValidIgnoredEnvelope(), ServerFixture.Json)
            : client.GetAsync(path);

    public static IEnumerable<object[]> ProtectedRoutes => new[]
    {
        new object[] { "GET", "/api/terminals" },
        new object[] { "GET", "/api/catalog/snapshot" },
        new object[] { "POST", "/api/sync/inbox" },
        new object[] { "GET", "/api/reports/day" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task ProtectedRoute_WithoutBearer_Returns401(string method, string path)
    {
        using var anonymous = _fx.AnonymousClient();

        var response = await Hit(anonymous, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path}");
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task ProtectedRoute_WithWrongBearer_Returns401(string method, string path)
    {
        using var wrong = _fx.AnonymousClient("not-the-key");

        var response = await Hit(wrong, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path}");
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task ProtectedRoute_WithCorrectBearer_Returns200(string method, string path)
    {
        var response = await Hit(_fx.Client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{method} {path}");
    }

    [Fact]
    public async Task Heartbeat_WithoutBearer_Returns401_AndWritesNothing()
    {
        using var anonymous = _fx.AnonymousClient();

        var response = await anonymous.PostAsJsonAsync("/api/terminals/heartbeat",
            new TerminalHeartbeatRequest("CAJA-02", "Caja 2", TerminalKind.Satellite), ServerFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _fx.GetJsonAsync("/api/terminals")).AsArray().Should().HaveCount(1);
    }

    [Theory]
    [InlineData("/api/catalog/products")]
    [InlineData("/api/catalog/categories")]
    [InlineData("/kds")]
    [InlineData("/health")]
    public async Task OpenRoutes_StayOpen_WithoutBearer(string path)
    {
        using var anonymous = _fx.AnonymousClient();

        var response = await anonymous.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
    }
}
