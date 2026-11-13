using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;

namespace Infrastructure.Data.Outbox;

/// <summary>
/// Builders for the Outbox payloads the master replicates (contract: <see cref="SyncEventTypes"/> XML doc).
/// camelCase, enums as names. Each payload carries the nested aggregate object the inbox needs plus the legacy
/// flat keys older webhook consumers already read.
/// </summary>
internal static class OutboxPayloads
{
    /// <summary>Web defaults + enums as names: the shape SyncInboxService (and any ERP) expects.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// <c>{ order: { ..., items: [...], payments: [...] }, orderId, orderNumber, total, paymentMethod, cashShiftId, completedAt, ... }</c>.
    /// <paramref name="order"/> must have Items and Payments loaded. <paramref name="extra"/> adds writer-specific
    /// legacy keys (comanda number, table) to the top level.
    /// </summary>
    public static string OrderCompleted(Order order, int? shiftNumber, DateTime completedAt, object? extra = null)
    {
        var method = order.Payments.OrderBy(p => p.ProcessedAt).Select(p => (PaymentMethod?)p.Method).FirstOrDefault();
        var body = new
        {
            order = new
            {
                id = order.Id,
                orderNumber = order.OrderNumber,
                status = order.Status,
                subtotal = order.Subtotal,
                taxTotal = order.TaxTotal,
                discountTotal = order.DiscountTotal,
                total = order.Total,
                notes = order.Notes,
                customerId = order.CustomerId,
                cashShiftId = order.CashShiftId,
                shiftNumber,
                terminalId = order.TerminalId,
                terminalCode = order.TerminalCode,
                createdAt = order.CreatedAt,
                completedAt,
                items = order.Items.Select(i => new
                {
                    id = i.Id, productId = i.ProductId, productName = i.ProductName, unitPrice = i.UnitPrice,
                    quantity = i.Quantity, weightKg = i.WeightKg, taxRate = i.TaxRate, taxAmount = i.TaxAmount,
                    subtotal = i.Subtotal, total = i.Total
                }).ToList(),
                payments = order.Payments.Select(p => new
                {
                    id = p.Id, method = p.Method, amount = p.Amount, tenderedAmount = p.TenderedAmount,
                    changeAmount = p.ChangeAmount, reference = p.Reference, processedAt = p.ProcessedAt
                }).ToList()
            },
            // legacy flat keys
            orderId = order.Id,
            orderNumber = order.OrderNumber,
            total = order.Total,
            paymentMethod = method?.ToString(),
            customerId = order.CustomerId,
            cashShiftId = order.CashShiftId,
            terminalId = order.TerminalId,
            terminalCode = order.TerminalCode,
            completedAt
        };

        if (extra == null) return Serialize(body);

        // Merge the writer-specific keys into the top-level object.
        using var doc = JsonDocument.Parse(Serialize(body));
        using var extraDoc = JsonDocument.Parse(Serialize(extra));
        using var stream = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject()) prop.WriteTo(w);
            foreach (var prop in extraDoc.RootElement.EnumerateObject()) prop.WriteTo(w);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary><c>{ billPayment: {...} }</c> plus legacy flat keys.</summary>
    public static string BillPayment(BillPayment p, object? extra = null)
    {
        var body = new
        {
            billPayment = new
            {
                id = p.Id, cashShiftId = p.CashShiftId, terminalId = p.TerminalId, terminalCode = p.TerminalCode,
                billerId = p.BillerId, billerName = p.BillerName, accountReference = p.AccountReference,
                customerName = p.CustomerName, amount = p.Amount, commission = p.Commission, method = p.Method,
                tenderedAmount = p.TenderedAmount, changeAmount = p.ChangeAmount, receiptNumber = p.ReceiptNumber,
                status = p.Status, userId = p.UserId, userName = p.UserName, paidAt = p.PaidAt,
                voidedAt = p.VoidedAt, voidOverrideId = p.VoidOverrideId, voidReason = p.VoidReason
            },
            billPaymentId = p.Id, receiptNumber = p.ReceiptNumber, shiftId = p.CashShiftId, billerId = p.BillerId,
            billerName = p.BillerName, accountReference = p.AccountReference, amount = p.Amount,
            commission = p.Commission, total = p.Total, method = p.Method.ToString(), userName = p.UserName,
            paidAt = p.PaidAt, voidedAt = p.VoidedAt, reason = p.VoidReason, managerOverrideId = p.VoidOverrideId
        };
        if (extra == null) return Serialize(body);

        using var doc = JsonDocument.Parse(Serialize(body));
        using var extraDoc = JsonDocument.Parse(Serialize(extra));
        using var stream = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject()) prop.WriteTo(w);
            foreach (var prop in extraDoc.RootElement.EnumerateObject()) prop.WriteTo(w);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
