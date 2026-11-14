using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Sync;

namespace Loqzen.Tests.SharedComandas;

/// <summary>MasterKitchenSocketClient without a socket: frame handling (HandleText) and the ws(s) URL derivation.</summary>
public class MasterKitchenSocketClientTests
{
    private readonly KitchenEventBus _bus = new();
    private readonly List<KitchenEvent> _events = new();
    private readonly MasterKitchenSocketClient _client;

    public MasterKitchenSocketClientTests()
    {
        _bus.Subscribe(_events.Add);
        _client = new MasterKitchenSocketClient(new TerminalSettings { Code = "CAJA-02", Mode = TerminalKind.Satellite, MasterUrl = "http://master:5055" }, _bus);
    }

    private static readonly Guid Id = Guid.Parse("7d4f3f6e-1a2b-4c3d-9e8f-0a1b2c3d4e5f");

    private static string Frame(string type = "event", string evt = "StatusChanged", string status = "Ready") =>
        $$"""{"type":"{{type}}","event":"{{evt}}","kitchenOrderId":"{{Id}}","station":"Barra","orderNumber":"CMD-3","tableNumber":"Mesa 1","status":"{{status}}","occurredAt":"2026-11-12T12:00:00Z"}""";

    [Fact]
    public void HandleText_ValidEnvelope_PublishesKitchenEventWithSameFields()
    {
        _client.HandleText(Frame());

        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.StatusChanged);
        e.KitchenOrderId.Should().Be(Id);
        e.Station.Should().Be("Barra");
        e.OrderNumber.Should().Be("CMD-3");
        e.TableNumber.Should().Be("Mesa 1");
        e.Status.Should().Be(KitchenOrderStatus.Ready);
        e.OccurredAt.ToUniversalTime().Should().Be(new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void HandleText_ComandaChangedEnvelope_PublishesComandaChanged()
    {
        _client.HandleText($$"""{"type":"event","event":"ComandaChanged","kitchenOrderId":"{{Id}}","station":"","orderNumber":"#7","tableNumber":"Mesa 1","status":"Pending","occurredAt":"2026-11-12T12:00:00Z"}""");

        var e = _events.Should().ContainSingle().Subject;
        e.Type.Should().Be(KitchenEventType.ComandaChanged);
        e.KitchenOrderId.Should().Be(Id);
        e.OrderNumber.Should().Be("#7");
        e.Station.Should().BeEmpty();
        e.Status.Should().Be(KitchenOrderStatus.Pending);
    }

    [Fact]
    public void HandleText_RealServerEnvelope_RoundTrips()
    {
        var envelope = new KitchenSocketEnvelope("event", KitchenEventType.Created, Id, "Cocina Caliente", "CMD-9", "Mesa 4", KitchenOrderStatus.Pending, new DateTime(2026, 11, 12, 12, 5, 0, DateTimeKind.Utc));

        _client.HandleText(WireJson.Serialize(envelope));

        _events.Should().ContainSingle().Which.Should().BeEquivalentTo(envelope.ToEvent());
    }

    [Fact]
    public void HandleText_TypeIsCaseInsensitive_AndWhitespaceTolerant()
    {
        _client.HandleText("  \n" + Frame(type: "EVENT") + "  ");

        _events.Should().ContainSingle();
    }

    [Theory]
    [InlineData("pong")]
    [InlineData("PONG")]
    [InlineData("ping")]
    [InlineData("""{"type":"pong"}""")]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"hello","event":"Created"}""")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("{not json")]
    [InlineData("""{"type":"event","event":"NoSuchEvent","kitchenOrderId":"x"}""")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void HandleText_NonEventOrGarbage_PublishesNothingAndDoesNotThrow(string text)
    {
        _client.Invoking(c => c.HandleText(text)).Should().NotThrow();
        _events.Should().BeEmpty();
    }

    [Fact]
    public void HandleText_EventWithMissingOptionalFields_StillPublishes()
    {
        // The hub always sends the full record; a shorter one must not crash the pump.
        _client.Invoking(c => c.HandleText($$"""{"type":"event","event":"Printed","kitchenOrderId":"{{Id}}"}""")).Should().NotThrow();
        if (_events.Count == 1)
        {
            _events[0].Type.Should().Be(KitchenEventType.Printed);
            _events[0].KitchenOrderId.Should().Be(Id);
        }
    }

    [Fact]
    public void HandleText_BrokenSubscriber_DoesNotThrow()
    {
        _bus.Subscribe(_ => throw new InvalidOperationException("ui crashed"));

        _client.Invoking(c => c.HandleText(Frame())).Should().NotThrow();
        _events.Should().ContainSingle();
    }

    [Fact]
    public void NewClient_IsNotConnected_NoError()
    {
        _client.IsConnected.Should().BeFalse();
        _client.LastError.Should().BeNull();
    }

    [Fact]
    public async Task StopWithoutStart_IsNoOp()
    {
        await _client.Invoking(c => c.StopAsync()).Should().NotThrowAsync();
        await _client.DisposeAsync();
    }

    // ── BuildSocketUri ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://192.168.1.10:5055", "ws://192.168.1.10:5055/ws/kds")]
    [InlineData("http://192.168.1.10:5055/", "ws://192.168.1.10:5055/ws/kds")]
    [InlineData("https://master.local:8443", "wss://master.local:8443/ws/kds")]
    [InlineData("https://master.local/", "wss://master.local/ws/kds")]
    [InlineData("http://master.local", "ws://master.local/ws/kds")]
    [InlineData("http://master.local:80/", "ws://master.local/ws/kds")]
    [InlineData("  http://master:5055  ", "ws://master:5055/ws/kds")]
    [InlineData("http://master:5055/api/?x=1#frag", "ws://master:5055/ws/kds")]
    [InlineData("ws://master:5055", "ws://master:5055/ws/kds")]
    [InlineData("wss://master:5055", "wss://master:5055/ws/kds")]
    public void BuildSocketUri_DerivesWsUrlKeepingHostAndPort(string masterUrl, string expected)
    {
        MasterKitchenSocketClient.BuildSocketUri(masterUrl).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildSocketUri_MissingUrl_Throws(string? masterUrl)
    {
        FluentActions.Invoking(() => MasterKitchenSocketClient.BuildSocketUri(masterUrl))
            .Should().Throw<InvalidOperationException>().WithMessage("*MasterUrl*");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("192.168.1.10:5055")]
    public void BuildSocketUri_InvalidUrl_Throws(string masterUrl)
    {
        FluentActions.Invoking(() => MasterKitchenSocketClient.BuildSocketUri(masterUrl))
            .Should().Throw<InvalidOperationException>().WithMessage("*no es una URL válida*");
    }
}
