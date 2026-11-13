using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Receipts;

/// <summary>Z report, recaudo receipt and movement slip: 32 columns, ASCII, key sections.</summary>
public class CashReportsTests
{
    private const string Store = "Panadería Ñapa del Río";
    private static readonly DateTime T0 = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

    private static ShiftReconciliationDto ClosedShift() => new()
    {
        ShiftId = Guid.NewGuid(),
        ShiftNumber = 17,
        CashierName = "María José Pérez Gómez de la Torre",
        Status = CashShiftStatus.Closed,
        OpenedAt = T0,
        ClosedAt = T0.AddHours(9),
        ClosedByName = "María José",
        OpeningAmount = 200000m,
        CashSales = 1250000m,
        CardSales = 830500m,
        TransferSales = 120000m,
        OtherSales = 5000m,
        OrderCount = 143,
        BillPaymentCount = 12,
        BillPaymentsCash = 1015500m,
        BillPaymentsCard = 51500m,
        BillPaymentsTransfer = 0m,
        BillPaymentCommissions = 19500m,
        BillPaymentsToRemit = 1047500m,
        PaidIn = 50000m,
        PaidOut = 120000m,
        Expenses = 18500m,
        SafeDrops = 1500000m,
        TipPayouts = 45000m,
        Movements =
        {
            new CashMovementDto { Type = CashMovementType.PaidOut, Amount = 120000m, Reason = "Pago proveedor de lácteos y quesos artesanales", OccurredAt = T0.AddHours(2) },
            new CashMovementDto { Type = CashMovementType.BillPayment, Amount = 51500m, Reason = "Recaudo Energía", OccurredAt = T0.AddHours(3) },
        },
        CountedCash = 830000m,
        FloatLeftAmount = 200000m,
        WasBlindCount = true,
        DifferenceApprovedBy = "Gerente Ñoño",
        Notes = "Faltante por billete roto; se repone mañana con soporte del banco y firma del cajero.",
        CountLines =
        {
            new CashCountLineDto { Denomination = 100000m, Quantity = 5 },
            new CashCountLineDto { Denomination = 50000m, Quantity = 6 },
            new CashCountLineDto { Denomination = 1000m, Quantity = 30 },
        },
        PartialCounts =
        {
            new PartialCountDto { ExpectedAmount = 900000m, CountedAmount = 899000m, UserName = "Supervisor", CountedAt = T0.AddHours(4) }
        }
    };

    private static BillPaymentDto Bill(BillPaymentStatus status = BillPaymentStatus.Completed, PaymentMethod method = PaymentMethod.Cash) => new()
    {
        Id = Guid.NewGuid(),
        ReceiptNumber = "REC-000123",
        BillerName = "Energía Eléctrica del Caribe S.A. E.S.P.",
        AccountReference = "0012345678901234567890123456789012345",
        CustomerName = "José Núñez",
        Amount = 187350m,
        Commission = 1500m,
        Method = method,
        TenderedAmount = 200000m,
        ChangeAmount = 11150m,
        Status = status,
        UserName = "Cajero",
        PaidAt = T0,
        VoidedAt = status == BillPaymentStatus.Voided ? T0.AddMinutes(10) : null,
        VoidReason = status == BillPaymentStatus.Voided ? "Referencia equivocada, el cliente trajo la factura del vecino" : null
    };

    private static void AssertPrintable(IReadOnlyList<ReceiptLine> lines)
    {
        lines.Should().NotBeEmpty();
        foreach (var l in lines)
        {
            var max = l.IsLarge ? ReceiptFormatter.DefaultWidth / 2 : ReceiptFormatter.DefaultWidth;
            l.Text.Length.Should().BeLessThanOrEqualTo(max, $"line '{l.Text}'");
            l.Text.Length.Should().BeLessThanOrEqualTo(32);
            l.Text.Should().MatchRegex("^[\\x20-\\x7E]*$", $"printer encodes ASCII: '{l.Text}'");
        }
    }

    private static string Text(IReadOnlyList<ReceiptLine> lines) => ReceiptFormatter.ToPlainText(lines);

    [Fact]
    public void ZReport_ClosedShift_FitsWidth_AsciiOnly_HasKeySections()
    {
        var lines = CashReports.ZReport(ClosedShift(), Store);
        AssertPrintable(lines);
        var t = Text(lines);

        t.Should().Contain("Panaderia Napa del Rio");
        t.Should().Contain("CIERRE DE TURNO #17");
        t.Should().NotContain("CORTE PARCIAL");
        foreach (var s in new[] { "VENTAS", "RECAUDOS", "MOVIMIENTOS", "EFECTIVO", "DIFERENCIA", "(faltante)", "Contado:", "Esperado:", "Arqueo:", "Arqueos parciales:", "Autorizo: Gerente Nono", "Firma cajero", "Otros:", "A girar a convenios:" })
            t.Should().Contain(s);
        t.Should().Contain("Pago proveedor", "manual movements are itemised");
        t.Should().NotContain("Recaudo Energia", "bill-payment movements are not itemised as manual movements");
    }

    [Fact]
    public void ZReport_PrintsExpectedAndDifferenceAmounts()
    {
        var r = ClosedShift();
        var t = Text(CashReports.ZReport(r, Store));
        t.Should().Contain(ReceiptFormatter.Cop(r.ExpectedCash));
        t.Should().Contain(ReceiptFormatter.Cop(r.Difference!.Value));
    }

    [Fact]
    public void ZReport_OpenShift_PrintsCortePartial_WithoutCountedSection()
    {
        var r = ClosedShift();
        r.Status = CashShiftStatus.Open;
        r.ClosedAt = null;
        r.ClosedByName = null;
        r.CountedCash = null;
        r.CountLines.Clear();

        var lines = CashReports.ZReport(r, Store);
        AssertPrintable(lines);
        var t = Text(lines);

        t.Should().Contain("CORTE PARCIAL");
        t.Should().NotContain("CIERRE DE TURNO");
        t.Should().NotContain("Contado:");
        t.Should().NotContain("DIFERENCIA");
        t.Should().Contain("Esperado:");
    }

    [Fact]
    public void ZReport_ExactMatch_SaysCuadreExacto()
    {
        var r = ClosedShift();
        r.CountedCash = r.ExpectedCash;
        Text(CashReports.ZReport(r, Store)).Should().Contain("cuadre exacto");
    }

    [Fact]
    public void BillPaymentReceipt_Cash_FitsWidth_AndHasKeyTexts()
    {
        var lines = CashReports.BillPaymentReceipt(Bill(), Store);
        AssertPrintable(lines);
        var t = Text(lines);

        t.Should().Contain("COMPROBANTE DE RECAUDO");
        t.Should().Contain("REC-000123");
        t.Should().Contain("no es");
        t.Should().Contain("factura de venta");
        t.Should().Contain("Recibido:");
        t.Should().Contain("Cambio:");
        t.Should().Contain("Jose Nunez");
        t.Should().Contain(ReceiptFormatter.Cop(188850m), "total = amount + commission");
        t.Should().NotContain("ANULADO");
    }

    [Fact]
    public void BillPaymentReceipt_Card_HasNoChangeBlock()
    {
        var t = Text(CashReports.BillPaymentReceipt(Bill(method: PaymentMethod.Card), Store));
        t.Should().Contain("Tarjeta");
        t.Should().NotContain("Cambio:");
    }

    [Fact]
    public void BillPaymentReceipt_Voided_SaysAnulado_WithReason()
    {
        var lines = CashReports.BillPaymentReceipt(Bill(BillPaymentStatus.Voided), Store);
        AssertPrintable(lines);
        var t = Text(lines);
        t.Should().Contain("ANULADO");
        t.Should().Contain("Motivo:");
    }

    [Theory]
    [InlineData(CashMovementType.SafeDrop, "RETIRO A BOVEDA", "-$")]
    [InlineData(CashMovementType.PaidIn, "ENTRADA DE EFECTIVO", "$50.000")]
    public void MovementSlip_FitsWidth_AndHasKeyTexts(CashMovementType type, string label, string amountText)
    {
        var m = new CashMovementDto
        {
            Type = type,
            Amount = 50000m,
            Reason = "Retiro programado del mediodía para consignación bancaria",
            Reference = "SOBRE-0001",
            UserName = "Cajero Ñañez",
            ManagerOverrideId = Guid.NewGuid(),
            OccurredAt = T0
        };

        var lines = CashReports.MovementSlip(m, 17, Store);
        AssertPrintable(lines);
        var t = Text(lines);

        t.Should().Contain("MOVIMIENTO DE CAJA");
        t.Should().Contain(label);
        t.Should().Contain(amountText);
        t.Should().Contain("#17");
        t.Should().Contain("SOBRE-0001");
        t.Should().Contain("Autorizado por gerente");
        t.Should().Contain("Firma");
    }

    [Fact]
    public void MovementSlip_WithoutOverride_OmitsAuthorizedLine()
    {
        var m = new CashMovementDto { Type = CashMovementType.PaidIn, Amount = 1000m, Reason = "Sencillo", UserName = "A", OccurredAt = T0 };
        Text(CashReports.MovementSlip(m, 1, Store)).Should().NotContain("Autorizado");
    }
}
