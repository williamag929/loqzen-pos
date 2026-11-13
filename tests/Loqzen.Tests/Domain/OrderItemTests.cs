using Core.Domain.Entities;
using FluentAssertions;

namespace Loqzen.Tests.Domain;

/// <summary>
/// Unit tests for OrderItem domain entity calculation logic.
/// </summary>
public class OrderItemTests
{
    [Fact]
    public void Subtotal_CalculatesUnitPriceTimesQuantity()
    {
        // Arrange
        var item = new OrderItem { UnitPrice = 10.00m, Quantity = 3, TaxRate = 0.19m };

        // Act
        item.Subtotal = item.UnitPrice * item.Quantity;

        // Assert
        item.Subtotal.Should().Be(30.00m);
    }

    [Fact]
    public void Subtotal_WithWeightedProduct_CalculatesCorrectly()
    {
        // Arrange - weighted product (e.g., meat at $15/kg for 1.5kg)
        var item = new OrderItem { UnitPrice = 15.00m, Quantity = 1.5m, WeightKg = 1.5m };

        // Act
        item.Subtotal = item.UnitPrice * item.Quantity;

        // Assert
        item.Subtotal.Should().Be(22.50m);
    }

    [Fact]
    public void TaxAmount_CalculatesFromSubtotalAndRate()
    {
        // Arrange
        var item = new OrderItem { UnitPrice = 100.00m, Quantity = 1, TaxRate = 0.19m };

        // Act
        item.Subtotal = item.UnitPrice * item.Quantity;
        item.TaxAmount = Math.Round(item.Subtotal * item.TaxRate, 2);
        item.Total = item.Subtotal + item.TaxAmount;

        // Assert
        item.TaxAmount.Should().Be(19.00m);
        item.Total.Should().Be(119.00m);
    }

    [Fact]
    public void TaxAmount_WithZeroRate_IsZero()
    {
        // Arrange - exempt product
        var item = new OrderItem { UnitPrice = 50.00m, Quantity = 2, TaxRate = 0m };

        // Act
        item.Subtotal = item.UnitPrice * item.Quantity;
        item.TaxAmount = Math.Round(item.Subtotal * item.TaxRate, 2);
        item.Total = item.Subtotal + item.TaxAmount;

        // Assert
        item.TaxAmount.Should().Be(0m);
        item.Total.Should().Be(100.00m);
    }

    [Fact]
    public void FractionalQuantity_CalculatesCorrectly()
    {
        // Arrange - e.g., 2.5 items at $8 each
        var item = new OrderItem { UnitPrice = 8.00m, Quantity = 2.5m, TaxRate = 0.05m };

        // Act
        item.Subtotal = item.UnitPrice * item.Quantity;
        item.TaxAmount = Math.Round(item.Subtotal * item.TaxRate, 2);
        item.Total = item.Subtotal + item.TaxAmount;

        // Assert
        item.Subtotal.Should().Be(20.00m);
        item.TaxAmount.Should().Be(1.00m);
        item.Total.Should().Be(21.00m);
    }
}
