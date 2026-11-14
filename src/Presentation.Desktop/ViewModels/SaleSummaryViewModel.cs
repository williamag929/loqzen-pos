using System;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Receipts;
using Core.Domain.Enums;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

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
        _dismissLabel = dismissLabel;
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
    private readonly string? _dismissLabel;

    // Short-lived VM (one per paid sale): texts are read from the i18n tables when the overlay opens; no live re-raise.
    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);

    /// <summary>Caption of the big button ("🛒 Nueva venta"; recaudos use "🧾 Nuevo recaudo").</summary>
    public string DismissLabel => _dismissLabel ?? T("Sale.Completed.NewSale");

    public string Title => _title ?? (string.IsNullOrWhiteSpace(TableName)
        ? T("Sale.Completed.Title", OrderNumber)
        : T("Sale.Completed.TableTitle", TableName, OrderNumber));

    /// <summary>Big label above the figure.</summary>
    public string Headline => HasChange ? T("Sale.Completed.Change") : IsCash ? T("Sale.Completed.Exact") : T("Sale.Completed.NoChange", ReceiptDtoFactory.PaymentName(Method).ToUpperInvariant());

    /// <summary>The figure the cashier must hand back (or the total when there is no change).</summary>
    public string BigFigure => ReceiptFormatter.Cop(HasChange ? Change : Total);
    /// <summary>Theme token (amber = change to hand back, green = nothing to return); read when the overlay opens.</summary>
    public string BigFigureColor => HasChange ? ThemePalette.WarningBright : ThemePalette.SuccessBright;

    public string TotalText => ReceiptFormatter.Cop(Total);
    public string TenderedText => ReceiptFormatter.Cop(Tendered);
    public string MethodText => ReceiptDtoFactory.PaymentName(Method);
}
