using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>Cuadre de caja: summary by payment method + close with denomination count.</summary>
public class CashCountTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly CashShiftService _service;

    public CashCountTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new CashShiftService(_db);
    }

    public void Dispose() => _db.Dispose();

    private void AddSale(Guid shiftId, PaymentMethod method, decimal amount)
    {
        var order = new Order { OrderNumber = Guid.NewGuid().ToString("N"), CashShiftId = shiftId, Status = OrderStatus.Completed, Total = amount };
        order.Payments.Add(new Payment { Method = method, Amount = amount });
        _db.Orders.Add(order);
        _db.SaveChanges();
    }

    [Fact]
    public async Task Summary_SplitsSalesByPaymentMethod()
    {
        var shift = await _service.OpenShiftAsync("Caja", 100000m);
        AddSale(shift.Id, PaymentMethod.Cash, 30000m);
        AddSale(shift.Id, PaymentMethod.Cash, 20000m);
        AddSale(shift.Id, PaymentMethod.Card, 45000m);
        AddSale(shift.Id, PaymentMethod.Transfer, 15000m);

        var s = await _service.GetCloseSummaryAsync();

        s.CashSales.Should().Be(50000m);
        s.CardSales.Should().Be(45000m);
        s.TransferSales.Should().Be(15000m);
        s.TotalSales.Should().Be(110000m);
        s.ExpectedCash.Should().Be(150000m);
        s.OrderCount.Should().Be(4);
    }

    [Fact]
    public async Task CloseWithCount_ComputesClosingFromDenominations_AndPersistsLines()
    {
        var shift = await _service.OpenShiftAsync("Caja", 100000m);
        AddSale(shift.Id, PaymentMethod.Cash, 50000m);

        var closed = await _service.CloseShiftWithCountAsync(new[]
        {
            new CashCountLineDto { Denomination = 50000m, Quantity = 2 },
            new CashCountLineDto { Denomination = 20000m, Quantity = 2 },
            new CashCountLineDto { Denomination = 1000m, Quantity = 5 },
            new CashCountLineDto { Denomination = 500m, Quantity = 0 }
        });

        closed.ClosingAmount.Should().Be(145000m);
        closed.ExpectedAmount.Should().Be(150000m);
        closed.Difference.Should().Be(-5000m, "faltante");
        closed.Status.Should().Be(CashShiftStatus.Closed);
        (await _db.CashCountLines.CountAsync(l => l.CashShiftId == shift.Id)).Should().Be(3, "zero-quantity lines are skipped");
    }

    [Fact]
    public async Task CloseWithCount_BlockedWhileComandasOpen()
    {
        await _service.OpenShiftAsync("Caja", 0m);
        _db.Comandas.Add(new Comanda { Number = 1, TableName = "Mesa 1", Status = ComandaStatus.Open });
        await _db.SaveChangesAsync();

        var act = () => _service.CloseShiftWithCountAsync(Array.Empty<CashCountLineDto>());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*comanda*");
    }

    [Fact]
    public async Task CloseWithCount_NegativeQuantity_Throws()
    {
        await _service.OpenShiftAsync("Caja", 0m);
        var act = () => _service.CloseShiftWithCountAsync(new[] { new CashCountLineDto { Denomination = 1000m, Quantity = -1 } });
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
