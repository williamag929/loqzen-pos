using System.Net;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Sync;

namespace Loqzen.Tests.SharedComandas;

/// <summary>Satellite RemoteKitchenService over the master's /api/kds/* (KitchenOrderWire ⇄ KitchenOrder).</summary>
public class RemoteKitchenServiceTests
{
    private readonly FakeMasterHandler _master = new();
    private readonly RemoteKitchenService _svc;

    public RemoteKitchenServiceTests()
    {
        _svc = new RemoteKitchenService(new HttpClient(_master), new TerminalSettings { Code = "CAJA-02", Mode = TerminalKind.Satellite, MasterUrl = "http://master:5055", ApiKey = "k" });
    }

    private static KitchenOrder Sample(KitchenOrderStatus status = KitchenOrderStatus.InPreparation)
    {
        var ko = new KitchenOrder
        {
            Id = Guid.NewGuid(), OrderNumber = "CMD-12", OrderId = Guid.NewGuid(), TableNumber = "Mesa 5", WaiterId = Guid.NewGuid(), WaiterName = "Ana",
            Status = status, Station = "Barra", GeneralNotes = "rápido",
            CreatedAt = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc), UpdatedAt = new DateTime(2026, 11, 12, 12, 1, 0, DateTimeKind.Utc),
            PrintedAt = new DateTime(2026, 11, 12, 12, 2, 0, DateTimeKind.Utc), DeliveredAt = null
        };
        ko.Items.Add(new KitchenOrderItem { Id = Guid.NewGuid(), KitchenOrderId = ko.Id, ProductId = Guid.NewGuid(), ProductName = "Gaseosa", Quantity = 2, Notes = "sin hielo", IsPrepared = true, IsCancelled = false, CreatedAt = new DateTime(2026, 11, 12, 12, 0, 5, DateTimeKind.Utc) });
        ko.Items.Add(new KitchenOrderItem { Id = Guid.NewGuid(), KitchenOrderId = ko.Id, ProductId = Guid.NewGuid(), ProductName = "Jugo", Quantity = 1.5m, Notes = null, IsPrepared = false, IsCancelled = true, CreatedAt = new DateTime(2026, 11, 12, 12, 0, 6, DateTimeKind.Utc) });
        return ko;
    }

    private static void AssertSameAs(KitchenOrder actual, KitchenOrder expected)
    {
        actual.Should().BeEquivalentTo(expected, o => o
            .Excluding(k => k.Items)
            .Excluding(k => k.UpdatedAt)); // UpdatedAt travels too, checked below
        actual.UpdatedAt.Should().Be(expected.UpdatedAt);
        actual.Items.Should().HaveCount(expected.Items.Count);
        foreach (var (a, e) in actual.Items.Zip(expected.Items))
        {
            a.Should().BeEquivalentTo(e, o => o.Excluding(i => i.KitchenOrder).Excluding(i => i.UpdatedAt));
            a.KitchenOrderId.Should().Be(expected.Id);
        }
    }

    [Fact]
    public async Task GetActive_NoStation_GetsApiKdsOrders_AndRebuildsEntities()
    {
        var a = Sample();
        var b = Sample(KitchenOrderStatus.Ready);
        _master.On(HttpMethod.Get, "/api/kds/orders", new[] { KitchenOrderWire.From(a), KitchenOrderWire.From(b) });

        var list = await _svc.GetActiveAsync();

        var req = _master.Requests.Single();
        req.PathAndQuery.Should().Be("/api/kds/orders");
        req.Authorization!.Parameter.Should().Be("k");
        list.Should().HaveCount(2);
        AssertSameAs(list[0], a);
        AssertSameAs(list[1], b);
    }

    [Theory]
    [InlineData("Barra", "/api/kds/orders?station=Barra")]
    [InlineData("  Cocina Caliente ", "/api/kds/orders?station=Cocina%20Caliente")]
    public async Task GetActive_WithStation_AddsEscapedQuery(string station, string expected)
    {
        _master.On(HttpMethod.Get, "/api/kds/orders", Array.Empty<KitchenOrderWire>());

        var list = await _svc.GetActiveAsync(station);

        list.Should().BeEmpty();
        _master.Requests.Single().PathAndQuery.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetActive_BlankStation_NoQuery(string station)
    {
        _master.On(HttpMethod.Get, "/api/kds/orders", Array.Empty<KitchenOrderWire>());

        await _svc.GetActiveAsync(station);

        _master.Requests.Single().PathAndQuery.Should().Be("/api/kds/orders");
    }

    [Fact]
    public async Task GetById_GetsApiKdsOrdersId()
    {
        var ko = Sample();
        _master.On(HttpMethod.Get, $"/api/kds/orders/{ko.Id}", KitchenOrderWire.From(ko));

        var got = await _svc.GetByIdAsync(ko.Id);

        got.Should().NotBeNull();
        AssertSameAs(got!, ko);
        _master.Requests.Single().PathAndQuery.Should().Be($"/api/kds/orders/{ko.Id}");
    }

    [Fact]
    public async Task GetById_404_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _master.OnError(HttpMethod.Get, $"/api/kds/orders/{id}", HttpStatusCode.NotFound, "Ticket no encontrado.");

        (await _svc.GetByIdAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task GetById_Offline_ThrowsHttpRequestException()
    {
        var id = Guid.NewGuid();
        _master.OnThrow(HttpMethod.Get, $"/api/kds/orders/{id}", new HttpRequestException("refused"));

        await _svc.Invoking(s => s.GetByIdAsync(id))
            .Should().ThrowAsync<HttpRequestException>().WithMessage("Sin conexión con la caja principal*");
    }

    [Fact]
    public async Task Create_ThrowsInvalidOperation_WithoutRequest()
    {
        await _svc.Invoking(s => s.CreateAsync(Sample())).Should().ThrowAsync<InvalidOperationException>().WithMessage("*caja principal*");
        _master.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("advance", KitchenOrderStatus.Ready)]
    [InlineData("printed", KitchenOrderStatus.InPreparation)]
    [InlineData("undo", KitchenOrderStatus.Ready)]
    public async Task Mutations_PostTheActionRoute_AndReturnTheWireEntity(string action, KitchenOrderStatus returned)
    {
        var ko = Sample(returned);
        _master.On(HttpMethod.Post, $"/api/kds/orders/{ko.Id}/{action}", KitchenOrderWire.From(ko));

        var result = action switch
        {
            "advance" => await _svc.AdvanceAsync(ko.Id),
            "printed" => await _svc.MarkPrintedAsync(ko.Id),
            _ => await _svc.UndoDeliverAsync(ko.Id)
        };

        var req = _master.Requests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.PathAndQuery.Should().Be($"/api/kds/orders/{ko.Id}/{action}");
        req.Body.Should().BeNull();
        result.Status.Should().Be(returned);
        AssertSameAs(result, ko);
    }

    [Fact]
    public async Task Advance_409_MapsToInvalidOperation()
    {
        var id = Guid.NewGuid();
        _master.OnError(HttpMethod.Post, $"/api/kds/orders/{id}/advance", HttpStatusCode.Conflict, "El ticket ya fue entregado.");

        await _svc.Invoking(s => s.AdvanceAsync(id))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("El ticket ya fue entregado.");
    }

    [Fact]
    public async Task Advance_404_MapsToKeyNotFound()
    {
        var id = Guid.NewGuid();
        _master.OnError(HttpMethod.Post, $"/api/kds/orders/{id}/advance", HttpStatusCode.NotFound, "Ticket no encontrado.");

        await _svc.Invoking(s => s.AdvanceAsync(id)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Advance_Offline_ThrowsHttpRequestException()
    {
        var id = Guid.NewGuid();
        _master.OnThrow(HttpMethod.Post, $"/api/kds/orders/{id}/advance", new HttpRequestException("refused"));

        await _svc.Invoking(s => s.AdvanceAsync(id))
            .Should().ThrowAsync<HttpRequestException>().WithMessage("Sin conexión con la caja principal: refused");
    }

    [Fact]
    public async Task GetActive_ServerJsonShape_IsCamelCaseWithStringEnums()
    {
        // Exactly what the master's /api/kds/orders emits (web defaults + JsonStringEnumConverter).
        var id = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var json = $$"""
        [{"id":"{{id}}","orderNumber":"CMD-3","orderId":null,"tableNumber":"Mesa 2","waiterId":null,"waiterName":"Ana",
          "status":"Ready","station":"Cocina Caliente","generalNotes":null,"createdAt":"2026-11-12T12:00:00Z","updatedAt":null,
          "printedAt":null,"deliveredAt":null,
          "items":[{"id":"{{itemId}}","productId":"{{productId}}","productName":"Hamburguesa","quantity":2,"notes":"sin cebolla","isPrepared":false,"isCancelled":false,"createdAt":"2026-11-12T12:00:01Z"}]}]
        """;
        _master.On(HttpMethod.Get, "/api/kds/orders", _ => FakeMasterHandler.Raw(json));

        var list = await _svc.GetActiveAsync();

        var ko = list.Should().ContainSingle().Subject;
        ko.Id.Should().Be(id);
        ko.Status.Should().Be(KitchenOrderStatus.Ready);
        ko.Station.Should().Be("Cocina Caliente");
        ko.OrderId.Should().BeNull();
        ko.CreatedAt.ToUniversalTime().Should().Be(new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));
        var item = ko.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be(itemId);
        item.KitchenOrderId.Should().Be(id);
        item.ProductId.Should().Be(productId);
        item.Quantity.Should().Be(2);
        item.Notes.Should().Be("sin cebolla");
    }
}
