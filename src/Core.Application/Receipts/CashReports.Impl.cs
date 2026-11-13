using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Core.Application.Receipts;

public static partial class CashReports
{
    /// <summary>Z report (cierre de turno). Works for open shifts too (counted section is omitted).</summary>
    public static IReadOnlyList<ReceiptLine> ZReport(ShiftReconciliationDto r, string storeName, int width = ReceiptFormatter.DefaultWidth)
    {
        var b = new Builder(width);
        b.Header(storeName);
        b.Center(r.Status == CashShiftStatus.Closed ? $"CIERRE DE TURNO #{r.ShiftNumber}" : $"CORTE PARCIAL TURNO #{r.ShiftNumber}", ReceiptLineStyle.Bold);
        b.Text($"Cajero:  {r.CashierName}");
        b.Text($"Apertura: {Local(r.OpenedAt)}");
        if (r.ClosedAt is { } closed) b.Text($"Cierre:   {Local(closed)}");
        if (!string.IsNullOrWhiteSpace(r.ClosedByName)) b.Text($"Cerro:   {r.ClosedByName}");

        b.Section("VENTAS");
        b.Cols("Efectivo:", Cop(r.CashSales));
        b.Cols("Tarjeta:", Cop(r.CardSales));
        b.Cols("Transferencia:", Cop(r.TransferSales));
        if (r.OtherSales != 0) b.Cols("Otros:", Cop(r.OtherSales));
        b.Thin();
        b.Cols("Total ventas:", Cop(r.TotalSales), ReceiptLineStyle.Bold);
        b.Cols("# Ordenes:", r.OrderCount.ToString(CultureInfo.InvariantCulture));

        b.Section("RECAUDOS");
        b.Cols("# Recaudos:", r.BillPaymentCount.ToString(CultureInfo.InvariantCulture));
        b.Cols("Efectivo:", Cop(r.BillPaymentsCash));
        b.Cols("Tarjeta:", Cop(r.BillPaymentsCard));
        b.Cols("Transferencia:", Cop(r.BillPaymentsTransfer));
        b.Cols("Comisiones:", Cop(r.BillPaymentCommissions));
        b.Cols("A girar a convenios:", Cop(r.BillPaymentsToRemit), ReceiptLineStyle.Bold);

        b.Section("MOVIMIENTOS");
        b.Cols("Entradas:", Cop(r.PaidIn));
        b.Cols("Salidas:", Neg(r.PaidOut));
        b.Cols("Gastos:", Neg(r.Expenses));
        b.Cols("Retiros a boveda:", Neg(r.SafeDrops));
        b.Cols("Propinas:", Neg(r.TipPayouts));
        if (r.BillPaymentVoidsCash != 0) b.Cols("Anul. recaudos:", Neg(r.BillPaymentVoidsCash));
        var manual = r.Movements.Where(m => CashMovementTypes.Manual.Contains(m.Type)).ToList();
        if (manual.Count > 0)
        {
            b.Thin();
            foreach (var m in manual)
            {
                b.Cols($"{Local(m.OccurredAt, "HH:mm")} {m.Reason}", Cop(m.SignedAmount));
            }
        }

        b.Section("EFECTIVO");
        b.Cols("Base apertura:", Cop(r.OpeningAmount));
        b.Cols("Esperado:", Cop(r.ExpectedCash), ReceiptLineStyle.Bold);
        if (r.CountedCash is { } counted)
        {
            b.Cols("Contado:", Cop(counted), ReceiptLineStyle.Bold);
            var diff = r.Difference ?? 0m;
            b.Text("DIFERENCIA " + (diff switch { > 0 => "(sobrante)", < 0 => "(faltante)", _ => "(cuadre exacto)" }), ReceiptLineStyle.Bold);
            b.Large("", Cop(diff));
            b.Cols("Fondo dejado:", Cop(r.FloatLeftAmount));
            b.Cols("Conteo a ciegas:", r.WasBlindCount ? "Si" : "No");
            if (!string.IsNullOrWhiteSpace(r.DifferenceApprovedBy)) b.Text($"Autorizo: {r.DifferenceApprovedBy}");
            if (!string.IsNullOrWhiteSpace(r.Notes)) b.Wrap($"Notas: {r.Notes}");

            if (r.CountLines.Count > 0)
            {
                b.Thin();
                b.Text("Arqueo:", ReceiptLineStyle.Bold);
                foreach (var l in r.CountLines.OrderByDescending(l => l.Denomination))
                    b.Cols($"{l.Quantity,4} x {Cop(l.Denomination)}", Cop(l.Total));
            }
        }

        if (r.PartialCounts.Count > 0)
        {
            b.Thin();
            b.Text("Arqueos parciales:", ReceiptLineStyle.Bold);
            foreach (var p in r.PartialCounts.OrderBy(p => p.CountedAt))
            {
                b.Text($"{Local(p.CountedAt, "HH:mm")} {p.UserName}");
                b.Cols("  Esp/Cont:", $"{Cop(p.ExpectedAmount)}/{Cop(p.CountedAmount)}");
                b.Cols("  Dif:", Cop(p.Difference));
            }
        }

        b.Sep();
        b.Blank();
        b.Text("Firma cajero: ______________");
        b.Blank();
        b.Text("Firma gerente: _____________");
        return b.Lines;
    }

    /// <summary>Customer receipt for a bill payment (recaudo). Not a sales invoice.</summary>
    public static IReadOnlyList<ReceiptLine> BillPaymentReceipt(BillPaymentDto p, string storeName, int width = ReceiptFormatter.DefaultWidth)
    {
        var b = new Builder(width);
        b.Header(storeName);
        b.Center("COMPROBANTE DE RECAUDO", ReceiptLineStyle.Bold);
        if (p.Status == BillPaymentStatus.Voided)
            b.Center("*** ANULADO ***", ReceiptLineStyle.Large);
        b.Text($"No.:     {p.ReceiptNumber}");
        b.Text($"Fecha:   {Local(p.PaidAt)}");
        b.Text($"Cajero:  {p.UserName}");
        b.Sep();
        b.Text($"Convenio: {p.BillerName}", ReceiptLineStyle.Bold);
        b.Wrap($"Ref.:    {p.AccountReference}");
        if (!string.IsNullOrWhiteSpace(p.CustomerName)) b.Wrap($"Cliente: {p.CustomerName}");
        b.Sep();
        b.Cols("Valor:", Cop(p.Amount));
        b.Cols("Comision:", Cop(p.Commission));
        b.Large("TOTAL:", Cop(p.Total));
        b.Sep();
        b.Text($"Medio: {MethodName(p.Method)}");
        if (p.Method == PaymentMethod.Cash)
        {
            b.Cols("Recibido:", Cop(p.TenderedAmount));
            b.Cols("Cambio:", Cop(p.ChangeAmount));
        }
        if (p.Status == BillPaymentStatus.Voided)
        {
            b.Thin();
            if (p.VoidedAt is { } v) b.Text($"Anulado: {Local(v)}");
            if (!string.IsNullOrWhiteSpace(p.VoidReason)) b.Wrap($"Motivo: {p.VoidReason}");
        }
        b.Sep();
        b.Blank();
        b.Center("Este comprobante no es");
        b.Center("factura de venta");
        return b.Lines;
    }

    /// <summary>Slip for a manual cash movement (kept in the drawer / signed by the receiver).</summary>
    public static IReadOnlyList<ReceiptLine> MovementSlip(CashMovementDto m, int shiftNumber, string storeName, int width = ReceiptFormatter.DefaultWidth)
    {
        var b = new Builder(width);
        b.Header(storeName);
        b.Center("MOVIMIENTO DE CAJA", ReceiptLineStyle.Bold);
        b.Center(m.TypeLabel.ToUpperInvariant());
        b.Sep();
        b.Large("VALOR:", (m.Sign < 0 ? "-" : "") + Cop(m.Amount));
        b.Sep();
        b.Wrap($"Motivo: {m.Reason}");
        if (!string.IsNullOrWhiteSpace(m.Reference)) b.Wrap($"Ref.:   {m.Reference}");
        b.Text($"Turno:  #{shiftNumber}");
        b.Text($"Usuario: {m.UserName}");
        b.Text($"Fecha:  {Local(m.OccurredAt)}");
        if (m.ManagerOverrideId != null) b.Text("Autorizado por gerente");
        b.Blank();
        b.Blank();
        b.Text("Firma: ______________________");
        b.Blank();
        return b.Lines;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Cop(decimal amount) => ReceiptFormatter.Cop(amount);

    /// <summary>Outflow shown with a minus sign ("-$10.000"); zero stays "$0".</summary>
    private static string Neg(decimal amount) => amount == 0 ? Cop(0) : "-" + Cop(amount);

    private static string Local(DateTime utc, string format = "yyyy-MM-dd HH:mm")
    {
        var u = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc;
        return u.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
    }

    private static string MethodName(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Efectivo",
        PaymentMethod.Card => "Tarjeta",
        PaymentMethod.Transfer => "Transferencia",
        _ => "Otro"
    };

    /// <summary>Small line builder; mirrors ReceiptFormatter's private Fit/Columns/Wrap (ASCII, fixed width).</summary>
    private sealed class Builder
    {
        private readonly int _w;
        public List<ReceiptLine> Lines { get; } = new();
        public Builder(int width) => _w = Math.Max(16, width);

        public void Header(string storeName)
        {
            Center($"*** {storeName} ***", ReceiptLineStyle.Bold);
            Sep();
        }

        public void Text(string text, ReceiptLineStyle style = ReceiptLineStyle.Normal) =>
            Lines.Add(new ReceiptLine(Fit(text, style == ReceiptLineStyle.Large ? _w / 2 : _w), style));

        public void Center(string text, ReceiptLineStyle style = ReceiptLineStyle.Normal) =>
            Lines.Add(new ReceiptLine(Fit(text, style == ReceiptLineStyle.Large ? _w / 2 : _w), style, ReceiptAlign.Center));

        public void Wrap(string text)
        {
            var t = ReceiptFormatter.Ascii(text);
            if (t.Length == 0) return;
            for (var i = 0; i < t.Length; i += _w)
                Lines.Add(new ReceiptLine(t.Substring(i, Math.Min(_w, t.Length - i))));
        }

        public void Cols(string left, string right, ReceiptLineStyle style = ReceiptLineStyle.Normal) =>
            Lines.Add(new ReceiptLine(Columns(left, right, _w), style));

        /// <summary>Double-size line: only half the columns fit.</summary>
        public void Large(string left, string right) =>
            Lines.Add(new ReceiptLine(Columns(left, right, _w / 2), ReceiptLineStyle.Large));

        public void Section(string title)
        {
            Sep();
            Lines.Add(new ReceiptLine(Fit(title, _w), ReceiptLineStyle.Bold));
            Thin();
        }

        public void Sep() => Lines.Add(new ReceiptLine(new string('=', _w)));
        public void Thin() => Lines.Add(new ReceiptLine(new string('-', _w)));
        public void Blank() => Lines.Add(new ReceiptLine(string.Empty));

        private static string Fit(string text, int width)
        {
            var t = ReceiptFormatter.Ascii(text);
            return t.Length > width ? t[..width] : t;
        }

        private static string Columns(string left, string right, int width)
        {
            left = ReceiptFormatter.Ascii(left);
            right = ReceiptFormatter.Ascii(right);
            if (right.Length >= width) return right[..width];
            var space = width - left.Length - right.Length;
            if (space < 1)
            {
                left = left[..Math.Max(0, width - right.Length - 1)];
                space = width - left.Length - right.Length;
            }
            return left + new string(' ', space) + right;
        }
    }
}
