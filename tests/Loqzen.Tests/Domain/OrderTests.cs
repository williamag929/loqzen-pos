using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Domain;

/// <summary>
/// Unit tests for Order domain entity total calculations.
/// </summary>
public class OrderTests
{
    [Fact]
    public void Total_SumsItemsMinusDiscount()
    {
        // Arrange
        var order = new Order { OrderNumber = "ORD-001", DiscountTotal = 5.00m };
        order.Items.Add(CreateItem(10.00m, 2, 0.19m));  // Sub 20, Tax 3.80
        order.Items.Add(CreateItem(15.00m, 1, 0.19m));  // Sub 15, Tax 2.85

        // Act
        RecalculateTotals(order);

        // Assert
        order.Subtotal.Should().Be(35.00m);
        order.TaxTotal.Should().Be(6.65m);
        order.Total.Should().Be(36.65m); // 35 + 6.65 - 5
    }

    [Fact]
    public void Total_WithEmptyItems_IsZero()
    {
        var order = new Order { OrderNumber = "ORD-002" };
        RecalculateTotals(order);

        order.Subtotal.Should().Be(0m);
        order.TaxTotal.Should().Be(0m);
        order.Total.Should().Be(0m);
    }

    [Fact]
    public void Total_WithMultipleTaxRates_CalculatesCorrectly()
    {
        var order = new Order { OrderNumber = "ORD-003" };
        order.Items.Add(CreateItem(100.00m, 1, 0.19m)); // Tax: 19
        order.Items.Add(CreateItem(50.00m, 2, 0.05m));  // Tax: 5
        order.Items.Add(CreateItem(30.00m, 1, 0m));     // Tax: 0

        RecalculateTotals(order);

        order.Subtotal.Should().Be(230.00m);
        order.TaxTotal.Should().Be(24.00m);
        order.Total.Should().Be(254.00m);
    }

    [Fact]
    public void DefaultStatus_IsDraft()
    {
        new Order().Status.Should().Be(OrderStatus.Draft);
    }

    private static OrderItem CreateItem(decimal price, decimal qty, decimal taxRate)
    {
        var item = new OrderItem { UnitPrice = price, Quantity = qty, TaxRate = taxRate };
        item.Subtotal = item.UnitPrice * item.Quantity;
        item.TaxAmount = Math.Round(item.Subtotal * item.TaxRate, 2);
        item.Total = item.Subtotal + item.TaxAmount;
        return item;
    }

    private static void RecalculateTotals(Order order)
    {
        order.Subtotal = order.Items.Sum(i => i.Subtotal);
        order.TaxTotal = order.Items.Sum(i => i.TaxAmount);
        order.Total = Math.Max(0, order.Subtotal + order.TaxTotal - order.DiscountTotal);
    }
}
