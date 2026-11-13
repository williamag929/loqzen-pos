using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;

namespace Loqzen.Tests.Fixtures;

/// <summary>Shared data builders for cash-shift / bill-payment tests (orders, overrides, billers, counts).</summary>
public static class CashOpsData
{
    public static Order AddSale(PosDbContext db, Guid shiftId, PaymentMethod method, decimal amount,
        OrderStatus status = OrderStatus.Completed)
    {
        var order = new Order
        {
            OrderNumber = "ORD-" + Guid.NewGuid().ToString("N")[..12],
            CashShiftId = shiftId,
            Status = status,
            Total = amount
        };
        order.Payments.Add(new Payment { Method = method, Amount = amount, TenderedAmount = amount });
        db.Orders.Add(order);
        db.SaveChanges();
        return order;
    }

    public static async Task<Guid> AddOverrideAsync(PosDbContext db, TimeProvider clock, AuthorizedAction action,
        string managerName = "Gerente")
    {
        var o = new ManagerOverride
        {
            Action = action,
            Description = action.ToString(),
            ManagerUserId = Guid.NewGuid(),
            ManagerName = managerName,
            RequestedByName = "Cajero",
            AuthorizedAt = clock.GetUtcNow().UtcDateTime
        };
        db.ManagerOverrides.Add(o);
        await db.SaveChangesAsync();
        return o.Id;
    }

    public static async Task<Biller> AddBillerAsync(PosDbContext db, string name, decimal fixedCommission = 0m,
        decimal percent = 0m, bool active = true)
    {
        var b = new Biller { Name = name, CommissionFixed = fixedCommission, CommissionPercent = percent, IsActive = active };
        db.Billers.Add(b);
        await db.SaveChangesAsync();
        return b;
    }

    /// <summary>Count lines whose total is exactly <paramref name="amount"/> (one line, denomination = amount).</summary>
    public static IReadOnlyList<CashCountLineDto> Count(decimal amount) =>
        amount == 0 ? Array.Empty<CashCountLineDto>() : new[] { new CashCountLineDto { Denomination = amount, Quantity = 1 } };

    public static CashMovementRequest Movement(CashMovementType type, decimal amount, string reason = "Motivo",
        Guid? overrideId = null, string? reference = null) =>
        new(type, amount, reason, reference, Guid.NewGuid(), "Cajero", overrideId);

    public static CloseShiftRequest Close(decimal counted, decimal floatLeft = 0m, string? notes = null,
        Guid? overrideId = null, bool blind = true, string closedBy = "Cajero Cierre", Guid? closedById = null) =>
        new(Count(counted), floatLeft, notes, closedById, closedBy, blind, overrideId);
}
