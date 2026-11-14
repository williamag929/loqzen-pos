using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.SharedComandas;

/// <summary>KitchenOrderWire / KitchenOrderItemWire / KitchenSocketEnvelope: pure mapping records.</summary>
public class KitchenWireTests
{
    private static KitchenOrder Sample()
    {
        var ko = new KitchenOrder
        {
            Id = Guid.NewGuid(), OrderNumber = "CMD-12", OrderId = Guid.NewGuid(), TableNumber = "Mesa 5", WaiterId = Guid.NewGuid(), WaiterName = "Ana",
            Status = KitchenOrderStatus.Delivered, Station = "Barra", GeneralNotes = "rápido",
            CreatedAt = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc), UpdatedAt = new DateTime(2026, 11, 12, 12, 1, 0, DateTimeKind.Utc),
            PrintedAt = new DateTime(2026, 11, 12, 12, 2, 0, DateTimeKind.Utc), DeliveredAt = new DateTime(2026, 11, 12, 12, 9, 0, DateTimeKind.Utc)
        };
        ko.Items.Add(new KitchenOrderItem { Id = Guid.NewGuid(), KitchenOrderId = ko.Id, ProductId = Guid.NewGuid(), ProductName = "Gaseosa", Quantity = 2, Notes = "sin hielo", IsPrepared = true, IsCancelled = false, CreatedAt = new DateTime(2026, 11, 12, 12, 0, 5, DateTimeKind.Utc) });
        ko.Items.Add(new KitchenOrderItem { Id = Guid.NewGuid(), KitchenOrderId = ko.Id, ProductId = Guid.NewGuid(), ProductName = "Jugo", Quantity = 1.5m, Notes = null, IsPrepared = false, IsCancelled = true, CreatedAt = new DateTime(2026, 11, 12, 12, 0, 6, DateTimeKind.Utc) });
        return ko;
    }

    [Fact]
    public void From_CopiesEveryField()
    {
        var ko = Sample();

        var w = KitchenOrderWire.From(ko);

        w.Should().BeEquivalentTo(new
        {
            ko.Id, ko.OrderNumber, ko.OrderId, ko.TableNumber, ko.WaiterId, ko.WaiterName, ko.Status, ko.Station, ko.GeneralNotes,
            ko.CreatedAt, ko.UpdatedAt, ko.PrintedAt, ko.DeliveredAt
        });
        w.Items.Should().HaveCount(2);
        var i = ko.Items.First();
        w.Items[0].Should().BeEquivalentTo(new { i.Id, i.ProductId, i.ProductName, i.Quantity, i.Notes, i.IsPrepared, i.IsCancelled, i.CreatedAt });
    }

    [Fact]
    public void From_ToEntity_RoundTripEqualsOriginal()
    {
        var ko = Sample();

        var back = KitchenOrderWire.From(ko).ToEntity();

        back.Should().BeEquivalentTo(ko, o => o.Excluding(k => k.Items));
        back.Items.Should().HaveCount(2);
        foreach (var (b, a) in back.Items.Zip(ko.Items))
        {
            b.Should().BeEquivalentTo(a, o => o.Excluding(x => x.KitchenOrder).Excluding(x => x.UpdatedAt));
            b.KitchenOrderId.Should().Be(ko.Id, "items are re-parented to the wire's id");
        }
    }

    [Fact]
    public void ToEntity_UsesWireIdForItemsParent()
    {
        var wireId = Guid.NewGuid();
        var item = new KitchenOrderItemWire(Guid.NewGuid(), Guid.NewGuid(), "X", 1, null, false, false, DateTime.UtcNow);
        var w = new KitchenOrderWire(wireId, "CMD-1", null, "Mesa 1", null, "Ana", KitchenOrderStatus.Pending, "Barra", null, DateTime.UtcNow, null, null, null, new[] { item });

        var ko = w.ToEntity();

        ko.Id.Should().Be(wireId);
        ko.Items.Single().KitchenOrderId.Should().Be(wireId);
        ko.Items.Single().Id.Should().Be(item.Id);
    }

    [Fact]
    public void Wire_JsonRoundTrip_CamelCaseStringEnums()
    {
        var ko = Sample();
        var json = WireJson.Serialize(KitchenOrderWire.From(ko));

        json.Should().Contain("\"orderNumber\":\"CMD-12\"").And.Contain("\"status\":\"Delivered\"").And.Contain("\"isCancelled\":true");
        var back = WireJson.Deserialize<KitchenOrderWire>(json).ToEntity();
        back.Should().BeEquivalentTo(ko, o => o.Excluding(k => k.Items));
        back.Items.Select(i => i.Id).Should().Equal(ko.Items.Select(i => i.Id));
    }

    [Fact]
    public void Envelope_ToEvent_CopiesEveryField()
    {
        var id = Guid.NewGuid();
        var at = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var env = new KitchenSocketEnvelope("event", KitchenEventType.ItemCancelled, id, "Cocina Caliente", "CMD-4", "Mesa 2", KitchenOrderStatus.Cancelled, at);

        var e = env.ToEvent();

        e.Should().Be(new KitchenEvent(KitchenEventType.ItemCancelled, id, "Cocina Caliente", "CMD-4", "Mesa 2", KitchenOrderStatus.Cancelled, at));
    }

    [Fact]
    public void Envelope_JsonRoundTrip()
    {
        var env = new KitchenSocketEnvelope("event", KitchenEventType.ComandaChanged, Guid.NewGuid(), "", "#8", "Mesa 2", KitchenOrderStatus.Pending, new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));

        var json = WireJson.Serialize(env);

        json.Should().Contain("\"type\":\"event\"").And.Contain("\"event\":\"ComandaChanged\"").And.Contain("\"status\":\"Pending\"");
        WireJson.Deserialize<KitchenSocketEnvelope>(json).Should().Be(env);
    }
}
