using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Core.Application.Kitchen;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Server;

/// <summary>
/// <c>ComandaChanged</c> over /ws/kds: it bypasses the station filter (every register's comanda list must refresh),
/// while ticket events keep honouring it. Then the satellite side: <see cref="Infrastructure.Data.Sync.MasterKitchenSocketClient"/>
/// connected to the master republishes the same event on the satellite's own bus.
/// </summary>
public class KdsWebSocketComandaTests : IAsyncLifetime
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(5);

    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private async Task<ClientWebSocket> ConnectAsync(string? station = null)
    {
        var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(Patient);
        await ws.ConnectAsync(_fx.WsUri(station), cts.Token);
        ws.State.Should().Be(WebSocketState.Open);
        return ws;
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? Soon);
        var buffer = new byte[8 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            result.MessageType.Should().Be(WebSocketMessageType.Text);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return sb.ToString();
        }
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text)
    {
        using var cts = new CancellationTokenSource(Soon);
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
    }

    private async Task<JsonNode> OpenComandaAsync(string table, string waiter = "Ana")
    {
        var response = await _fx.PostJsonAsync("/api/comandas", new { tableName = table, waiterName = waiter });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await _fx.ReadJsonAsync(response);
    }

    private async Task<Guid> ProductIdAsync(string name)
    {
        var products = (await _fx.GetJsonAsync("/api/catalog/products")).AsArray();
        return products.Single(p => p!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<Guid>();
    }

    [Fact]
    public async Task StationSocket_ReceivesComandaChanged_WhenAComandaIsOpened_ButNotACocinaCreated()
    {
        using var barra = await ConnectAsync("Barra");
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();

        var dto = await OpenComandaAsync("Mesa 5", "Luis");
        var comandaId = dto["id"]!.GetValue<Guid>();
        var number = dto["number"]!.GetValue<int>();

        var frame = JsonNode.Parse(await ReceiveTextAsync(barra))!;
        frame["type"]!.GetValue<string>().Should().Be("event");
        frame["event"]!.GetValue<string>().Should().Be("ComandaChanged");
        frame["kitchenOrderId"]!.GetValue<Guid>().Should().Be(comandaId, "for ComandaChanged the id is the comanda's");
        frame["orderNumber"]!.GetValue<string>().Should().Be($"#{number}");
        frame["tableNumber"]!.GetValue<string>().Should().Be("Mesa 5");
        frame["station"]!.GetValue<string>().Should().BeEmpty();
        frame["status"]!.GetValue<string>().Should().Be("Pending");
        frame["occurredAt"]!.GetValue<string>().Should().EndWith("Z");

        // A burger goes to Cocina Caliente: the Barra socket must NOT get that Created, only the ComandaChanged
        // that follows it. Frames are ordered per socket, so the first frame after the add must already be the
        // ComandaChanged of the add, and the next the ComandaChanged of the send (never a Created).
        var burger = await ProductIdAsync("Hamburguesa Especial");
        (await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId = burger, quantity = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterAdd = JsonNode.Parse(await ReceiveTextAsync(barra))!;
        afterAdd["event"]!.GetValue<string>().Should().Be("ComandaChanged");
        afterAdd["kitchenOrderId"]!.GetValue<Guid>().Should().Be(comandaId);

        (await _fx.PostJsonAsync($"/api/comandas/{comandaId}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterSend = JsonNode.Parse(await ReceiveTextAsync(barra))!;
        afterSend["event"]!.GetValue<string>().Should().Be("ComandaChanged", "the Cocina Caliente Created was filtered out");
        afterSend["kitchenOrderId"]!.GetValue<Guid>().Should().Be(comandaId);

        // Nothing else is queued for Barra: a ping is answered right away with pong, not with a stray Created.
        await SendTextAsync(barra, "ping");
        (await ReceiveTextAsync(barra)).Should().Be("pong");
    }

    [Fact]
    public async Task UnfilteredSocket_GetsBothTheCreatedAndTheComandaChanged_OnSend()
    {
        using var all = await ConnectAsync();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();

        var comandaId = (await OpenComandaAsync("Mesa 3"))["id"]!.GetValue<Guid>();
        JsonNode.Parse(await ReceiveTextAsync(all))!["event"]!.GetValue<string>().Should().Be("ComandaChanged");
        var soda = await ProductIdAsync("Gaseosa 400ml");
        await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId = soda, quantity = 1 });
        JsonNode.Parse(await ReceiveTextAsync(all))!["event"]!.GetValue<string>().Should().Be("ComandaChanged");

        await _fx.PostJsonAsync($"/api/comandas/{comandaId}/send", null);

        var first = JsonNode.Parse(await ReceiveTextAsync(all))!;
        var second = JsonNode.Parse(await ReceiveTextAsync(all))!;
        var events = new[] { first, second };
        var created = events.Single(e => e["event"]!.GetValue<string>() == "Created");
        created["station"]!.GetValue<string>().Should().Be("Barra");
        created["orderNumber"]!.GetValue<string>().Should().StartWith("CMD-");
        created["kitchenOrderId"]!.GetValue<Guid>().Should().NotBe(comandaId, "a ticket id");
        var changed = events.Single(e => e["event"]!.GetValue<string>() == "ComandaChanged");
        changed["kitchenOrderId"]!.GetValue<Guid>().Should().Be(comandaId);
        first["event"]!.GetValue<string>().Should().Be("Created", "tickets are published before the comanda-list refresh");
    }

    [Fact]
    public async Task MasterKitchenSocketClient_RepublishesComandaChanged_OnTheSatelliteBus()
    {
        await using var sat = await _fx.CreateSatelliteAsync();
        var client = sat.SocketClient;
        client.IsConnected.Should().BeFalse("not auto-started");

        var changes = 0;
        client.Changed += () => Interlocked.Increment(ref changes);
        client.Start();
        (await ServerFixture.WaitUntilAsync(() => client.IsConnected, Patient)).Should().BeTrue(client.LastError);
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();
        client.LastError.Should().BeNull();
        changes.Should().BeGreaterThanOrEqualTo(1);

        Guid comandaId = Guid.Empty;
        int number = 0;
        var received = await sat.WaitForBusEventAsync(
            e => e.Type == KitchenEventType.ComandaChanged,
            async () =>
            {
                var dto = await OpenComandaAsync("Mesa 5", "Luis");
                comandaId = dto["id"]!.GetValue<Guid>();
                number = dto["number"]!.GetValue<int>();
            },
            Patient);

        received.Should().NotBeNull("the master's ComandaChanged must reach the satellite's bus within 5 s");
        received!.KitchenOrderId.Should().Be(comandaId);
        received.OrderNumber.Should().Be($"#{number}");
        received.TableNumber.Should().Be("Mesa 5");
        received.Station.Should().BeEmpty();
        received.Status.Should().Be(KitchenOrderStatus.Pending);
        received.OccurredAt.Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);

        // Ticket events travel the same way (the satellite's KDS screen listens to this bus).
        var burger = await ProductIdAsync("Hamburguesa Especial");
        await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId = burger, quantity = 1 });
        var created = await sat.WaitForBusEventAsync(
            e => e.Type == KitchenEventType.Created,
            () => _fx.PostJsonAsync($"/api/comandas/{comandaId}/send", null),
            Patient);
        created.Should().NotBeNull();
        created!.Station.Should().Be("Cocina Caliente");
        created.TableNumber.Should().Be("Mesa 5");

        // Stop: disconnected, the master's hub drops the connection, idempotent.
        await client.StopAsync();
        client.IsConnected.Should().BeFalse();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 0, Patient)).Should().BeTrue();
        await client.StopAsync();
        client.IsConnected.Should().BeFalse();

        // After stop nothing reaches the satellite's bus anymore.
        var afterStop = await sat.WaitForBusEventAsync(
            e => e.Type == KitchenEventType.ComandaChanged,
            () => _fx.PostJsonAsync($"/api/comandas/{comandaId}/ready", null),
            TimeSpan.FromMilliseconds(500));
        afterStop.Should().BeNull();
    }

    [Fact]
    public async Task MasterKitchenSocketClient_WhenTheMasterIsDown_ReportsOffline_AndReconnectsWhenItIsBack()
    {
        await using var sat = await _fx.CreateSatelliteAsync();
        var client = sat.SocketClient;
        await _fx.Server.StopAsync();

        client.Start();

        (await ServerFixture.WaitUntilAsync(() => client.LastError != null, Patient)).Should().BeTrue();
        client.IsConnected.Should().BeFalse();
        client.LastError.Should().StartWith("Sin conexión con la caja principal");

        // Master back. Port 0 means a NEW ephemeral port, so the satellite's settings are repointed (BuildSocketUri reads
        // MasterUrl on every session). The backoff ladder is real time (1, 2, 4 s…): the next attempt lands within ~4 s.
        await _fx.Server.StartAsync();
        _fx.Server.IsRunning.Should().BeTrue();
        sat.TerminalSettings.MasterUrl = _fx.BaseUrl;
        (await ServerFixture.WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(10))).Should().BeTrue(client.LastError);
        client.LastError.Should().BeNull();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();

        await client.StopAsync();
        client.IsConnected.Should().BeFalse();
    }
}
