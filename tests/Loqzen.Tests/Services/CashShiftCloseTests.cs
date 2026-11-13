using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Services;

/// <summary>
/// Tests for closing cash shifts and calculating expected amounts.
/// </summary>
public class CashShiftCloseTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly CashShiftService _service;

    public CashShiftCloseTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new CashShiftService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CloseShiftAsync_SetsStatusToClosed()
    {
        await _service.OpenShiftAsync("Cajero", 100.00m);

        var closed = await _service.CloseShiftAsync(100.00m);

        closed.Status.Should().Be(CashShiftStatus.Closed);
        closed.ClosedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CloseShiftAsync_ThrowsWhenNoOpenShift()
    {
        var act = () => _service.CloseShiftAsync(100.00m);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*turno abierto*");
    }

    [Fact]
    public async Task CloseShiftAsync_CalculatesExpectedFromCashSales()
    {
        var shift = await _service.OpenShiftAsync("Cajero", 100.00m);

        // Simulate a cash sale
        var order = new Order
        {
            OrderNumber = "ORD-001",
            CashShiftId = shift.Id,
            Status = OrderStatus.Completed,
            Total = 50.00m
        };
        _db.Orders.Add(order);

        var payment = new Payment
        {
            OrderId = order.Id,
            Method = PaymentMethod.Cash,
            Amount = 50.00m,
            TenderedAmount = 50.00m
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        // Close shift
        var closed = await _service.CloseShiftAsync(150.00m);

        closed.ExpectedAmount.Should().Be(150.00m); // 100 opening + 50 cash
        closed.ClosingAmount.Should().Be(150.00m);
        closed.Difference.Should().Be(0m);
    }

    [Fact]
    public async Task CloseShiftAsync_IncludesNotes()
    {
        await _service.OpenShiftAsync("Cajero", 100.00m);

        var closed = await _service.CloseShiftAsync(95.00m, "Faltaron $5");

        closed.Notes.Should().Be("Faltaron $5");
    }

    [Fact]
    public async Task GetCashTotalForShiftAsync_SumsOnlyCashPayments()
    {
        var shift = await _service.OpenShiftAsync("Cajero", 100.00m);

        // Add cash payment
        var order1 = new Order { OrderNumber = "ORD-001", CashShiftId = shift.Id };
        _db.Orders.Add(order1);
        _db.Payments.Add(new Payment { OrderId = order1.Id, Method = PaymentMethod.Cash, Amount = 30.00m });

        // Add card payment (should be excluded)
        var order2 = new Order { OrderNumber = "ORD-002", CashShiftId = shift.Id };
        _db.Orders.Add(order2);
        _db.Payments.Add(new Payment { OrderId = order2.Id, Method = PaymentMethod.Card, Amount = 50.00m });

        await _db.SaveChangesAsync();

        var cashTotal = await _service.GetCashTotalForShiftAsync(shift.Id);

        cashTotal.Should().Be(30.00m);
    }
}
