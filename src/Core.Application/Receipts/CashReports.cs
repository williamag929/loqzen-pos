using System.Collections.Generic;
using Core.Application.DTOs;

namespace Core.Application.Receipts;

/// <summary>
/// 32-column layouts for the Z report (cierre de turno), the partial count slip and the bill-payment
/// receipt. Same ReceiptLine model as sales receipts, so the screen preview and the printer match.
/// </summary>
public static partial class CashReports
{
    // Implemented by the backend agent in CashReports.Impl.cs:
    // public static IReadOnlyList<ReceiptLine> ZReport(ShiftReconciliationDto r, string storeName, int width = ReceiptFormatter.DefaultWidth);
    // public static IReadOnlyList<ReceiptLine> BillPaymentReceipt(BillPaymentDto p, string storeName, int width = ReceiptFormatter.DefaultWidth);
    // public static IReadOnlyList<ReceiptLine> MovementSlip(CashMovementDto m, int shiftNumber, string storeName, int width = ReceiptFormatter.DefaultWidth);
}
