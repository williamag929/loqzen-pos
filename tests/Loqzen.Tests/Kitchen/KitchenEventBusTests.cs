using Core.Application.Kitchen;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Kitchen;

/// <summary>In-process pub/sub used by the desktop KDS and the LAN WebSocket hub.</summary>
public class KitchenEventBusTests
{
    private static KitchenEvent Event(KitchenEventType type = KitchenEventType.Created) =>
        new(type, Guid.NewGuid(), "Cocina Caliente", "CMD-1", "Mesa 1", KitchenOrderStatus.Pending,
            new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Publish_DeliversToSubscriber()
    {
        var bus = new KitchenEventBus();
        var received = new List<KitchenEvent>();
        using var sub = bus.Subscribe(received.Add);

        var e = Event();
        bus.Publish(e);

        received.Should().ContainSingle().Which.Should().BeSameAs(e);
    }

    [Fact]
    public void Publish_AfterDispose_DoesNotDeliver()
    {
        var bus = new KitchenEventBus();
        var received = new List<KitchenEvent>();
        var sub = bus.Subscribe(received.Add);
        bus.Publish(Event());

        sub.Dispose();
        bus.Publish(Event(KitchenEventType.Printed));

        received.Should().ContainSingle().Which.Type.Should().Be(KitchenEventType.Created);
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        var bus = new KitchenEventBus();
        var sub = bus.Subscribe(_ => { });
        sub.Dispose();
        var act = () => sub.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowingHandler_DoesNotPreventNextHandler_NorPublisher()
    {
        var bus = new KitchenEventBus();
        var secondCalled = false;
        using var first = bus.Subscribe(_ => throw new InvalidOperationException("broken subscriber"));
        using var second = bus.Subscribe(_ => secondCalled = true);

        var act = () => bus.Publish(Event());

        act.Should().NotThrow("a broken subscriber must never break the sale");
        secondCalled.Should().BeTrue();
    }

    [Fact]
    public void MultipleSubscribers_AllReceiveEveryEvent()
    {
        var bus = new KitchenEventBus();
        var a = new List<KitchenEvent>();
        var b = new List<KitchenEvent>();
        var c = new List<KitchenEvent>();
        using var sa = bus.Subscribe(a.Add);
        using var sb = bus.Subscribe(b.Add);
        using var sc = bus.Subscribe(c.Add);

        bus.Publish(Event(KitchenEventType.Created));
        bus.Publish(Event(KitchenEventType.StatusChanged));

        a.Select(e => e.Type).Should().Equal(KitchenEventType.Created, KitchenEventType.StatusChanged);
        b.Should().HaveCount(2);
        c.Should().HaveCount(2);
    }

    [Fact]
    public void Publish_WithoutSubscribers_DoesNothing()
    {
        var bus = new KitchenEventBus();
        var act = () => bus.Publish(Event());
        act.Should().NotThrow();
    }

    [Fact]
    public void Unsubscribing_OneSubscriber_KeepsTheOthers()
    {
        var bus = new KitchenEventBus();
        var a = new List<KitchenEvent>();
        var b = new List<KitchenEvent>();
        var sa = bus.Subscribe(a.Add);
        using var sb = bus.Subscribe(b.Add);

        sa.Dispose();
        bus.Publish(Event());

        a.Should().BeEmpty();
        b.Should().ContainSingle();
    }
}
