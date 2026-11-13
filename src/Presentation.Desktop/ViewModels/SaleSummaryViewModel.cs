using System;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Receipts;
using Core.Domain.Enums;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// "Cambio a entregar" screen shown right after a sale is paid (quick sale and comandas).
/// Stays on screen until the cashier taps "Nueva venta" so the change amount is never lost
/// behind the receipt. Same pattern as the customer display's Completed state.
/// </summary>
public sealed class SaleSummaryViewModel
{
    public SaleSummaryViewModel(string orderNumber, decimal total, decimal tendered, decimal change,
        PaymentMethod method, string? tableName, Action onDismiss, string? title = null, string? dismissLabel = null)
    {
        _title = title;
        DismissLabel = dismissLabel ?? "🛒 Nueva venta";
        OrderNumber = orderNumber;
        Total = total;
        Tendered = tendered;
        Change = Math.Max(0, change);
        Method = method;
        TableName = tableName;
        DismissCommand = new RelayCommand(onDismiss);
    }

    public string OrderNumber { get; }
    public decimal Total { get; }
    public decimal Tendered { get; }
    public decimal Change { get; }
    public PaymentMethod Method { get; }
    public string? TableName { get; }
    public IRelayCommand DismissCommand { get; }

    public bool IsCash => Method == PaymentMethod.Cash;
    public bool HasChange => IsCash && Change > 0;

    private readonly string? _title;

    /// <summary>Caption of the big button ("🛒 Nueva venta"; recaudos use "🧾 Nuevo recaudo").</summary>
    public string DismissLabel { get; }

    public string Title => _title ?? (string.IsNullOrWhiteSpace(TableName)
        ? $"✅ Venta completada · Factura {OrderNumber}"
        : $"✅ {TableName} cerrada · Factura {OrderNumber}");

    /// <summary>Big label above the figure.</summary>
    public string Headline => HasChange ? "CAMBIO A ENTREGAR" : IsCash ? "PAGO EXACTO · SIN CAMBIO" : $"{ReceiptDtoFactory.PaymentName(Method).ToUpperInvariant()} · SIN CAMBIO";

    /// <summary>The figure the cashier must hand back (or the total when there is no change).</summary>
    public string BigFigure => ReceiptFormatter.Cop(HasChange ? Change : Total);
    public string BigFigureColor => HasChange ? "#FACC15" : "#34D399";

    public string TotalText => ReceiptFormatter.Cop(Total);
    public string TenderedText => ReceiptFormatter.Cop(Tendered);
    public string MethodText => ReceiptDtoFactory.PaymentName(Method);
}
