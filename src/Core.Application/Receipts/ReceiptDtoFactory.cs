using System;
using System.Collections.Generic;
using System.Linq;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Core.Application.Receipts;

/// <summary>
/// Builds ReceiptDto from the different sale sources. Preview and final print must use the same
/// builder so the on-screen "tirilla" matches the paper.
/// </summary>
public static class ReceiptDtoFactory
{
    public static string PaymentName(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Efectivo",
        PaymentMethod.Card => "Tarjeta",
        PaymentMethod.Transfer => "Transferencia",
        _ => "Otro"
    };

    /// <summary>Quick-sale cart (CheckoutView). Includes in-cart returns and discount.</summary>
    public static ReceiptDto FromCart(IEnumerable<CartItemDto> cart, decimal discount, string orderNumber, string cashierName)
    {
        var items = cart.ToList();
        var subtotal = items.Sum(i => i.Subtotal);
        var tax = items.Sum(i => i.TaxAmount);
        return new ReceiptDto
        {
            OrderNumber = orderNumber,
            Date = DateTime.Now,
            CashierName = cashierName,
            Items = items.Select(i => new ReceiptItemDto
            {
                Name = i.Name,
                Quantity = i.IsWeighed ? (i.WeightKg ?? 1) : i.Quantity,
                UnitPrice = i.UnitPrice,
                Total = i.Total,
                IsReturn = i.IsReturn
            }).ToList(),
            Subtotal = subtotal,
            TaxTotal = tax,
            DiscountTotal = discount,
            Total = Math.Max(0, subtotal + tax - discount)
        };
    }

    /// <summary>Live pre-bill for an open comanda (not an invoice).</summary>
    public static ReceiptDto FromComanda(ComandaDto c, string cashierName) => new()
    {
        OrderNumber = $"COMANDA #{c.Number}",
        Date = DateTime.Now,
        CashierName = cashierName,
        TableName = c.TableName,
        Items = c.BillableItems.Select(i => new ReceiptItemDto
        {
            Name = i.ProductName,
            Quantity = i.EffectiveQuantity,
            UnitPrice = i.UnitPrice,
            Total = i.Total,
            Notes = i.Notes
        }).ToList(),
        Subtotal = c.Subtotal,
        TaxTotal = c.TaxTotal,
        Total = c.Total
    };

    /// <summary>
    /// Final invoice built from the PERSISTED order, so paper == database. The payment reference comes from the
    /// persisted payment (<see cref="OrderDto.PaymentReference"/>) unless the caller passes one; the voucher lines
    /// come from the terminal charge the caller holds (<see cref="TerminalPaymentDto.VoucherText"/>), when printing
    /// vouchers is enabled.
    /// </summary>
    public static ReceiptDto FromOrder(OrderDto o, string cashierName, string? tableName, PaymentMethod method, decimal tendered, decimal change,
        string? paymentReference = null, IEnumerable<string>? voucherLines = null) => new()
    {
        OrderNumber = o.OrderNumber,
        Date = DateTime.Now,
        CashierName = cashierName,
        TableName = tableName,
        Items = o.Items.Select(i => new ReceiptItemDto
        {
            Name = i.ProductName,
            Quantity = i.WeightKg ?? i.Quantity,
            UnitPrice = i.UnitPrice,
            Total = i.Total
        }).ToList(),
        Subtotal = o.Subtotal,
        TaxTotal = o.TaxTotal,
        DiscountTotal = o.DiscountTotal,
        Total = o.Total,
        PaymentMethodName = PaymentName(method),
        TenderedAmount = method == PaymentMethod.Cash ? tendered : 0,
        ChangeAmount = change,
        PaymentReference = method == PaymentMethod.Cash ? null
            : string.IsNullOrWhiteSpace(paymentReference) ? NullIfBlank(o.PaymentReference) : paymentReference.Trim(),
        VoucherLines = VoucherLinesFrom(voucherLines)
    };

    /// <summary>Splits a terminal's VoucherText ('\n'-joined) into receipt lines; null when empty.</summary>
    public static List<string>? VoucherLinesFromText(string? voucherText) =>
        string.IsNullOrWhiteSpace(voucherText) ? null
        : VoucherLinesFrom(voucherText.Split('\n').Select(l => l.TrimEnd('\r')));

    private static List<string>? VoucherLinesFrom(IEnumerable<string>? lines)
    {
        if (lines == null) return null;
        var list = lines.Where(l => l != null).ToList();
        return list.Count == 0 ? null : list;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
