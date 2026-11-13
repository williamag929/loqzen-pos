using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Core.Application.Kitchen;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Server;

/// <summary>
/// /ws/kds with a real ClientWebSocket: connection accounting, ping/pong, bus fan-out with station filter,
/// and a clean server stop while a tablet is still connected.
/// </summary>
public class KdsWebSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(2);

    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private async Task<ClientWebSocket> ConnectAsync(string? station = null)
    {
        var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(_fx.WsUri(station), cts.Token);
        ws.State.Should().Be(WebSocketState.Open);
        return ws;
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text)
    {
        using var cts = new CancellationTokenSource(Soon);
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
    }

    /// <summary>Reads one complete text frame or fails the test after <paramref name="timeout"/>.</summary>
    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? Soon);
        var buffer = new byte[8 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            result.MessageType.Should().Be(WebSocketMessageType.Text, "the hub only pushes text frames while open");
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return sb.ToString();
        }
    }

    private KitchenEvent Event(KitchenEventType type, string station, string orderNumber = "ORD-WS-1", string table = "Mesa 9") =>
        new(type, Guid.NewGuid(), station, orderNumber, table, KitchenOrderStatus.Pending, _fx.Clock.GetUtcNow().UtcDateTime);

    [Fact]
    public async Task Connect_CountsClient_AndPingGetsPong()
    {
        using var ws = await ConnectAsync();

        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();

        await SendTextAsync(ws, "ping");
        (await ReceiveTextAsync(ws)).Should().Be("pong");

        // Health reflects the same counter the hub keeps.
        (await _fx.GetJsonAsync("/health"))["connectedClients"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task BusEvent_IsPushedAsJson_WithCamelCaseAndStringEnums()
    {
        using var ws = await ConnectAsync();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();

        var e = Event(KitchenEventType.Created, "Barra", "ORD-WS-77", "Mesa 5");
        _fx.Bus.Publish(e);

        var message = JsonNode.Parse(await ReceiveTextAsync(ws))!;
        message["type"]!.GetValue<string>().Should().Be("kitchen");
        message["event"]!.GetValue<string>().Should().Be("Created");
        message["kitchenOrderId"]!.GetValue<Guid>().Should().Be(e.KitchenOrderId);
        message["station"]!.GetValue<string>().Should().Be("Barra");
        message["orderNumber"]!.GetValue<string>().Should().Be("ORD-WS-77");
        message["tableNumber"]!.GetValue<string>().Should().Be("Mesa 5");
        message["status"]!.GetValue<string>().Should().Be("Pending");
        message["occurredAt"]!.GetValue<string>().Should().EndWith("Z", "timestamps go out as UTC");
        message.AsObject().Select(kv => kv.Key).Should().OnlyContain(k => char.IsLower(k[0]));
    }

    [Fact]
    public async Task StationFilter_IsCaseInsensitive_AndSkipsOtherStations()
    {
        using var all = await ConnectAsync();
        using var cocina = await ConnectAsync("cocina caliente");
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 2, Soon)).Should().BeTrue();

        // 1) Barra event: only the unfiltered socket should see it.
        _fx.Bus.Publish(Event(KitchenEventType.Created, "Barra", "ORD-BARRA"));
        JsonNode.Parse(await ReceiveTextAsync(all))!["orderNumber"]!.GetValue<string>().Should().Be("ORD-BARRA");

        // 2) Cocina Caliente event: both see it. Since frames are ordered per socket, the FIRST frame the
        //    filtered socket receives must be the Cocina one - proving the Barra event was never sent to it.
        _fx.Bus.Publish(Event(KitchenEventType.StatusChanged, "Cocina Caliente", "ORD-COCINA"));

        var filtered = JsonNode.Parse(await ReceiveTextAsync(cocina))!;
        filtered["orderNumber"]!.GetValue<string>().Should().Be("ORD-COCINA");
        filtered["station"]!.GetValue<string>().Should().Be("Cocina Caliente");
        filtered["event"]!.GetValue<string>().Should().Be("StatusChanged");

        JsonNode.Parse(await ReceiveTextAsync(all))!["orderNumber"]!.GetValue<string>().Should().Be("ORD-COCINA");

        // 3) Still responsive after the fan-out (no stuck write lock).
        await SendTextAsync(cocina, "ping");
        (await ReceiveTextAsync(cocina)).Should().Be("pong");
    }

    [Fact]
    public async Task ClientClose_DropsConnectedClientsToZero()
    {
        var ws = await ConnectAsync();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();

        // CloseOutputAsync (send Close, don't wait for the reply) rather than CloseAsync: the hub answers the
        // close frame and then Abort()s the socket in its cleanup, so a client waiting for the reply sometimes
        // gets "connection reset by peer" before it reads the frame (server-side race, ~1 in 10 runs).
        // What this test asserts is the hub's connection accounting, which must not depend on that race.
        using (var cts = new CancellationTokenSource(Soon))
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 0, Soon)).Should().BeTrue();
        ws.Dispose();

        // The hub keeps working for new tablets after one left.
        using var again = await ConnectAsync();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();
        await SendTextAsync(again, "ping");
        (await ReceiveTextAsync(again)).Should().Be("pong");
    }

    [Fact]
    public async Task ClientAbort_IsCleanedUp_AndDoesNotBreakOtherSockets()
    {
        var dying = await ConnectAsync();
        using var survivor = await ConnectAsync();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 2, Soon)).Should().BeTrue();

        dying.Abort(); // tablet lost Wi-Fi: no close handshake
        dying.Dispose();

        // Broadcasting still reaches the survivor; the dead one is removed (either by its receive loop or by the failed send).
        _fx.Bus.Publish(Event(KitchenEventType.Printed, "Barra"));
        JsonNode.Parse(await ReceiveTextAsync(survivor))!["event"]!.GetValue<string>().Should().Be("Printed");

        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task ServerStop_WithOpenSocket_CompletesPromptly_AndClosesIt()
    {
        using var ws = await ConnectAsync();
        (await ServerFixture.WaitUntilAsync(() => _fx.Server.ConnectedClients == 1, Soon)).Should().BeTrue();

        var stop = _fx.Server.StopAsync();
        var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(stop, "StopAsync must not wait on the host shutdown timeout for idle tablets");
        await stop; // surface any exception
        _fx.Server.IsRunning.Should().BeFalse();
        _fx.Server.ConnectedClients.Should().Be(0);

        // The client observes the close (a Close frame or a dropped connection) instead of hanging.
        using var cts = new CancellationTokenSource(Soon);
        var buffer = new byte[1024];
        try
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            result.MessageType.Should().Be(WebSocketMessageType.Close);
        }
        catch (WebSocketException)
        {
            // connection torn down without a close frame: acceptable, the point is it did not hang
        }
        ws.State.Should().BeOneOf(WebSocketState.CloseReceived, WebSocketState.Closed, WebSocketState.Aborted);
    }
}
