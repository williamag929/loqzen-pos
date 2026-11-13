using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// /api/kds/* against the three tickets seeded by DbInitializer:
/// ORD-KDS-103 (Cocina Caliente, Pending, oldest), ORD-KDS-102 (Barra, InPreparation), ORD-KDS-101 (Cocina Caliente, Pending, newest).
/// </summary>
public class KdsEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private async Task<Guid> SeededTicketIdAsync(string orderNumber) =>
        await _fx.WithDbAsync(db => db.KitchenOrders.AsNoTracking().Where(k => k.OrderNumber == orderNumber).Select(k => k.Id).SingleAsync());

    private static DateTime ParseUtc(JsonNode? node) =>
        DateTime.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public async Task Orders_ListsSeededTickets_InArrivalOrder_WithItems()
    {
        var response = await _fx.Client.GetAsync("/api/kds/orders");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var orders = (await _fx.ReadJsonAsync(response)).AsArray();
        orders.Should().HaveCount(3);
        orders.Select(o => o!["orderNumber"]!.GetValue<string>()).Should().Equal("ORD-KDS-103", "ORD-KDS-102", "ORD-KDS-101");
        orders.Select(o => ParseUtc(o!["createdAt"])).Should().BeInAscendingOrder();

        // Timestamps must go out as UTC ("Z"), otherwise the browser KDS shows local-time drift.
        orders.Select(o => ParseUtc(o!["createdAt"]).Kind).Should().AllBeEquivalentTo(DateTimeKind.Utc);

        var first = orders[0]!;
        first["tableNumber"]!.GetValue<string>().Should().Be("Mesa 7");
        first["waiterName"]!.GetValue<string>().Should().Be("Juan P.");
        first["station"]!.GetValue<string>().Should().Be("Cocina Caliente");
        first["status"]!.GetValue<string>().Should().Be("Pending");
        first["printedAt"].Should().BeNull();
        first["deliveredAt"].Should().BeNull();
        first["items"]!.AsArray().Should().ContainSingle()
            .Which["productName"]!.GetValue<string>().Should().Be("Hamburguesa Especial");

        var newest = orders[2]!;
        newest["items"]!.AsArray().Select(i => i!["productName"]!.GetValue<string>())
            .Should().BeEquivalentTo("Hamburguesa Especial", "Pizza Personal Artesanal");
        var item = newest["items"]![0]!;
        item["id"]!.GetValue<Guid>().Should().NotBeEmpty();
        item["quantity"]!.GetValue<decimal>().Should().BeGreaterThan(0);
        item["isPrepared"]!.GetValue<bool>().Should().BeFalse();
        item["isCancelled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Orders_StationFilter_IsCaseInsensitive()
    {
        var barra = (await _fx.GetJsonAsync("/api/kds/orders?station=barra")).AsArray();
        barra.Should().ContainSingle().Which["station"]!.GetValue<string>().Should().Be("Barra");
        barra[0]!["orderNumber"]!.GetValue<string>().Should().Be("ORD-KDS-102");

        var cocina = (await _fx.GetJsonAsync("/api/kds/orders?station=" + Uri.EscapeDataString("COCINA CALIENTE"))).AsArray();
        cocina.Should().HaveCount(2);
        cocina.Select(o => o!["station"]!.GetValue<string>()).Should().AllBe("Cocina Caliente");

        var none = (await _fx.GetJsonAsync("/api/kds/orders?station=Postres")).AsArray();
        none.Should().BeEmpty();
    }

    [Fact]
    public async Task GetById_ReturnsTicket_Or404WithErrorBody()
    {
        var id = await SeededTicketIdAsync("ORD-KDS-102");

        var ok = await _fx.Client.GetAsync($"/api/kds/orders/{id}");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await _fx.ReadJsonAsync(ok);
        dto["id"]!.GetValue<Guid>().Should().Be(id);
        dto["status"]!.GetValue<string>().Should().Be("InPreparation");

        var missing = await _fx.Client.GetAsync($"/api/kds/orders/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(missing))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Advance_PendingTicket_MovesToInPreparation_AndPersists()
    {
        var id = await SeededTicketIdAsync("ORD-KDS-103");

        var response = await _fx.PostJsonAsync($"/api/kds/orders/{id}/advance", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await _fx.ReadJsonAsync(response);
        dto["id"]!.GetValue<Guid>().Should().Be(id);
        dto["status"]!.GetValue<string>().Should().Be("InPreparation");
        dto["items"]!.AsArray().Should().NotBeEmpty("the mutation response carries the full ticket");

        var stored = await _fx.WithDbAsync(db => db.KitchenOrders.AsNoTracking().SingleAsync(k => k.Id == id));
        stored.Status.Should().Be(KitchenOrderStatus.InPreparation);
        stored.UpdatedAt.Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Advance_PastDelivered_Returns409_AndDeliveredTicketLeavesTheActiveList()
    {
        var id = await SeededTicketIdAsync("ORD-KDS-101");

        var statuses = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var r = await _fx.PostJsonAsync($"/api/kds/orders/{id}/advance", null);
            r.StatusCode.Should().Be(HttpStatusCode.OK, $"advance #{i + 1}");
            statuses.Add((await _fx.ReadJsonAsync(r))["status"]!.GetValue<string>());
        }
        statuses.Should().Equal("InPreparation", "Ready", "Delivered");

        var fourth = await _fx.PostJsonAsync($"/api/kds/orders/{id}/advance", null);
        fourth.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(fourth))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

        var active = (await _fx.GetJsonAsync("/api/kds/orders")).AsArray();
        active.Select(o => o!["orderNumber"]!.GetValue<string>()).Should().Equal("ORD-KDS-103", "ORD-KDS-102");

        // Delivered tickets are still addressable by id (the archive window needs them).
        var byId = await _fx.GetJsonAsync($"/api/kds/orders/{id}");
        byId["status"]!.GetValue<string>().Should().Be("Delivered");
        ParseUtc(byId["deliveredAt"]).Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Advance_UnknownTicket_Returns404()
    {
        var response = await _fx.PostJsonAsync($"/api/kds/orders/{Guid.NewGuid()}/advance", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Printed_SetsPrintedAtOnce_ReprintKeepsOriginal()
    {
        var id = await SeededTicketIdAsync("ORD-KDS-102");
        var firstPrint = _fx.Clock.GetUtcNow().UtcDateTime;

        var r1 = await _fx.PostJsonAsync($"/api/kds/orders/{id}/printed", null);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        ParseUtc((await _fx.ReadJsonAsync(r1))["printedAt"]).Should().Be(firstPrint);

        _fx.Clock.Advance(TimeSpan.FromMinutes(2));

        var r2 = await _fx.PostJsonAsync($"/api/kds/orders/{id}/printed", null);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        ParseUtc((await _fx.ReadJsonAsync(r2))["printedAt"]).Should().Be(firstPrint, "a reprint keeps the first print time");

        var stored = await _fx.WithDbAsync(db => db.KitchenOrders.AsNoTracking().SingleAsync(k => k.Id == id));
        stored.PrintedAt.Should().Be(firstPrint);
    }

    [Fact]
    public async Task Undo_AfterDelivered_ReturnsToReady_AndClearsDeliveredAt()
    {
        var id = await SeededTicketIdAsync("ORD-KDS-103");
        for (var i = 0; i < 3; i++)
            (await _fx.PostJsonAsync($"/api/kds/orders/{id}/advance", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var undo = await _fx.PostJsonAsync($"/api/kds/orders/{id}/undo", null);

        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await _fx.ReadJsonAsync(undo);
        dto["status"]!.GetValue<string>().Should().Be("Ready");
        dto["deliveredAt"].Should().BeNull();

        var stored = await _fx.WithDbAsync(db => db.KitchenOrders.AsNoTracking().SingleAsync(k => k.Id == id));
        stored.Status.Should().Be(KitchenOrderStatus.Ready);
        stored.DeliveredAt.Should().BeNull();

        // Back in the active list.
        (await _fx.GetJsonAsync("/api/kds/orders")).AsArray()
            .Select(o => o!["id"]!.GetValue<Guid>()).Should().Contain(id);
    }

    [Fact]
    public async Task Undo_OnTicketNotDelivered_Returns409()
    {
        var id = await SeededTicketIdAsync("ORD-KDS-102"); // InPreparation

        var response = await _fx.PostJsonAsync($"/api/kds/orders/{id}/undo", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Settings_ReturnsInjectedKdsSettings_AndKnownStations()
    {
        var response = await _fx.Client.GetAsync("/api/kds/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await _fx.ReadJsonAsync(response);
        dto["warningMinutes"]!.GetValue<int>().Should().Be(_fx.KdsSettings.WarningMinutes);
        dto["maxPrepMinutes"]!.GetValue<int>().Should().Be(_fx.KdsSettings.MaxPrepMinutes);
        dto["archiveDelaySeconds"]!.GetValue<int>().Should().Be(_fx.KdsSettings.ArchiveDelaySeconds);

        var stations = dto["stations"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        stations.Should().Contain("Cocina Caliente").And.Contain("Barra");
        stations.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task KdsPage_IsHtml_WithTitle()
    {
        var response = await _fx.Client.GetAsync("/kds");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Content.Headers.ContentType.CharSet.Should().BeEquivalentTo("utf-8");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Loqzen KDS");
    }
}
