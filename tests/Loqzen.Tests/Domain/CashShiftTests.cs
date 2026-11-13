using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Domain;

/// <summary>
/// Unit tests for CashShift domain entity.
/// </summary>
public class CashShiftTests
{
    [Fact]
    public void Difference_CalculatesClosingMinusExpected()
    {
        var shift = new CashShift
        {
            OpeningAmount = 100.00m,
            ExpectedAmount = 350.00m, // Opening + cash sales
            ClosingAmount = 345.00m   // Actual count
        };

        shift.Difference.Should().Be(-5.00m); // Short by $5
    }

    [Fact]
    public void Difference_PositiveWhenOverage()
    {
        var shift = new CashShift
        {
            OpeningAmount = 100.00m,
            ExpectedAmount = 350.00m,
            ClosingAmount = 360.00m
        };

        shift.Difference.Should().Be(10.00m); // Overage of $10
    }

    [Fact]
    public void Difference_ZeroWhenBalanced()
    {
        var shift = new CashShift
        {
            OpeningAmount = 100.00m,
            ExpectedAmount = 350.00m,
            ClosingAmount = 350.00m
        };

        shift.Difference.Should().Be(0m);
    }

    [Fact]
    public void DefaultStatus_IsOpen()
    {
        new CashShift().Status.Should().Be(CashShiftStatus.Open);
    }

    [Fact]
    public void DefaultOpenedAt_IsUtcNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var shift = new CashShift();
        var after = DateTime.UtcNow.AddSeconds(1);

        shift.OpenedAt.Should().BeAfter(before).And.BeBefore(after);
    }
}
