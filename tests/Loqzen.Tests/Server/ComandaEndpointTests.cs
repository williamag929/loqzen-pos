using System.Net;
using System.Text.Json.Nodes;
using Core.Application.Kitchen;
using FluentAssertions;

namespace Loqzen.Tests.Server;

/// <summary>Comandera (waiter tablet) flow over HTTP: open → add item → send to kitchen → visible on the KDS.</summary>
public class ComandaEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private async Task<Guid> OpenAsync(string table, string? waiter = "Ana")
    {
        var response = await _fx.PostJsonAsync("/api/comandas", new { tableName = table, waiterName = waiter });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await _fx.ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    private async Task<Guid> SeededProductIdAsync(string name)
    {
        var products = (await _fx.GetJsonAsync("/api/catalog/products")).AsArray();
        return products.Single(p => p!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<Guid>();
    }

    [Fact]
    public async Task Open_Returns201_WithLocation_AndDto()
    {
        var response = await _fx.PostJsonAsync("/api/comandas", new { tableName = "Mesa 1", waiterName = "Ana" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await _fx.ReadJsonAsync(response);
        var id = dto["id"]!.GetValue<Guid>();
        id.Should().NotBeEmpty();
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().EndWith($"/api/comandas/{id}");

        dto["number"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(1);
        dto["tableName"]!.GetValue<string>().Should().Be("Mesa 1");
        dto["waiterName"]!.GetValue<string>().Should().Be("Ana");
        dto["status"]!.GetValue<string>().Should().Be("Open");
        dto["items"]!.AsArray().Should().BeEmpty();

        // Location is dereferenceable.
        var follow = await _fx.Client.GetAsync(response.Headers.Location);
        follow.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(follow))["id"]!.GetValue<Guid>().Should().Be(id);
    }

    [Fact]
    public async Task Open_WithoutWaiter_DefaultsToMesero()
    {
        var response = await _fx.PostJsonAsync("/api/comandas", new { tableName = "Mesa 3" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await _fx.ReadJsonAsync(response))["waiterName"]!.GetValue<string>().Should().Be("Mesero");
    }

    [Fact]
    public async Task Open_SameTableTwice_Returns409()
    {
        await OpenAsync("Mesa 1");

        var second = await _fx.PostJsonAsync("/api/comandas", new { tableName = "Mesa 1", waiterName = "Luis" });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(second))["error"]!.GetValue<string>().Should().Contain("Mesa 1");
    }

    [Fact]
    public async Task Open_WithoutTableName_Returns400()
    {
        var empty = await _fx.PostJsonAsync("/api/comandas", new { waiterName = "Ana" });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(empty))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

        var blank = await _fx.PostJsonAsync("/api/comandas", new { tableName = "   " });
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddItem_Returns200_WithPendingItem()
    {
        var comandaId = await OpenAsync("Mesa 1");
        var productId = await SeededProductIdAsync("Hamburguesa Especial");

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId, quantity = 2, notes = "sin cebolla" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await _fx.ReadJsonAsync(response);
        dto["id"]!.GetValue<Guid>().Should().Be(comandaId);
        var items = dto["items"]!.AsArray();
        items.Should().ContainSingle();
        var item = items[0]!;
        item["productId"]!.GetValue<Guid>().Should().Be(productId);
        item["productName"]!.GetValue<string>().Should().Be("Hamburguesa Especial");
        item["quantity"]!.GetValue<decimal>().Should().Be(2m);
        item["notes"]!.GetValue<string>().Should().Be("sin cebolla");
        item["status"]!.GetValue<string>().Should().Be("Pending");
        item["station"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        dto["pendingCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task AddItem_QuantityZero_Returns400()
    {
        var comandaId = await OpenAsync("Mesa 1");
        var productId = await SeededProductIdAsync("Gaseosa 400ml");

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId, quantity = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AddItem_WithoutProduct_Returns400()
    {
        var comandaId = await OpenAsync("Mesa 1");

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { quantity = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddItem_UnknownComanda_Returns404()
    {
        var productId = await SeededProductIdAsync("Gaseosa 400ml");

        var response = await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/items", new { productId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AddItem_UnknownProduct_Returns409()
    {
        var comandaId = await OpenAsync("Mesa 1");

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Send_ConfirmsItems_PublishesCreated_AndTicketAppearsOnKds()
    {
        var comandaId = await OpenAsync("Mesa 1");
        var burger = await SeededProductIdAsync("Hamburguesa Especial");
        (await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId = burger, quantity = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var published = new List<KitchenEvent>();
        using var subscription = _fx.Bus.Subscribe(e => { lock (published) published.Add(e); });

        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/send", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await _fx.ReadJsonAsync(response);
        var items = dto["items"]!.AsArray();
        items.Should().ContainSingle();
        items.Select(i => i!["status"]!.GetValue<string>()).Should().AllBe("Confirmed");
        dto["pendingCount"]!.GetValue<int>().Should().Be(0);

        // Bus: exactly one Created event for the new ticket (published after the commit, so it's on the bus by the time the HTTP response arrives).
        KitchenEvent[] events;
        lock (published) events = published.ToArray();
        events.Should().ContainSingle();
        events[0].Type.Should().Be(KitchenEventType.Created);
        events[0].TableNumber.Should().Be("Mesa 1");
        events[0].OccurredAt.Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);

        // KDS: the ticket is now in the active list and matches the event.
        var kds = (await _fx.GetJsonAsync("/api/kds/orders")).AsArray();
        var ticket = kds.Single(o => o!["tableNumber"]!.GetValue<string>() == "Mesa 1")!;
        ticket["id"]!.GetValue<Guid>().Should().Be(events[0].KitchenOrderId);
        ticket["station"]!.GetValue<string>().Should().Be(events[0].Station);
        ticket["status"]!.GetValue<string>().Should().Be("Pending");
        ticket["items"]!.AsArray().Should().ContainSingle()
            .Which["productName"]!.GetValue<string>().Should().Be("Hamburguesa Especial");
    }

    [Fact]
    public async Task Send_UnknownComanda_Returns404()
    {
        var response = await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/send", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_ReturnsActiveComandas_AndGetById_404ForUnknown()
    {
        (await _fx.GetJsonAsync("/api/comandas")).AsArray().Should().BeEmpty();

        var a = await OpenAsync("Mesa 1");
        var b = await OpenAsync("Mesa 2", "Luis");

        var list = (await _fx.GetJsonAsync("/api/comandas")).AsArray();
        list.Select(c => c!["id"]!.GetValue<Guid>()).Should().BeEquivalentTo(new[] { a, b });
        list.Select(c => c!["tableName"]!.GetValue<string>()).Should().BeEquivalentTo("Mesa 1", "Mesa 2");

        var missing = await _fx.Client.GetAsync($"/api/comandas/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(missing))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }
}
