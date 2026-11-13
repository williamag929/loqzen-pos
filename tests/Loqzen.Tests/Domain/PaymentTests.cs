using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Domain;

/// <summary>
/// Unit tests for Payment domain entity.
/// </summary>
public class PaymentTests
{
    [Fact]
    public void Change_CalculatesCorrectly()
    {
        var payment = new Payment
        {
            Method = PaymentMethod.Cash,
            Amount = 50.00m,
            TenderedAmount = 100.00m
        };
        payment.ChangeAmount = payment.TenderedAmount - payment.Amount;

        payment.ChangeAmount.Should().Be(50.00m);
    }

    [Fact]
    public void ExactAmount_HasZeroChange()
    {
        var payment = new Payment
        {
            Method = PaymentMethod.Cash,
            Amount = 75.50m,
            TenderedAmount = 75.50m
        };
        payment.ChangeAmount = payment.TenderedAmount - payment.Amount;

        payment.ChangeAmount.Should().Be(0m);
    }

    [Fact]
    public void CardPayment_TenderedEqualsAmount()
    {
        var payment = new Payment
        {
            Method = PaymentMethod.Card,
            Amount = 150.00m,
            TenderedAmount = 150.00m
        };
        payment.ChangeAmount = payment.TenderedAmount - payment.Amount;

        payment.ChangeAmount.Should().Be(0m);
    }
}
