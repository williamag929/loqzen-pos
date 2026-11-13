using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>Receipt side of a terminal charge: "Ref:" line under the payment method, voucher lines, Z report, DTO factory.</summary>
public class TerminalReceiptTests
{
    private const string Ref = "AUTH 123456 · VISA ****4242";
    private const string PrintedRef = "Ref: AUTH 123456 - VISA ****4242";

    private static ReceiptDto Sale(string? reference = Ref, List<string>? voucher = null, string method = "Tarjeta") => new()
    {
        StoreName = "Tienda",
        OrderNumber = "FAC-1",
        CashierName = "Ana",
        Date = new DateTime(2026, 11, 12, 10, 0, 0),
        Items = { new ReceiptItemDto { Name = "Gaseosa", Quantity = 1, UnitPrice = 5000, Total = 5950 } },
        Subtotal = 5000, TaxTotal = 950, Total = 5950,
        PaymentMethodName = method,
        PaymentReference = reference,
        VoucherLines = voucher
    };

    private static List<string> Texts(IEnumerable<ReceiptLine> lines) => lines.Select(l => l.Text).ToList();

    // ── ReceiptFormatter ─────────────────────────────────────────────────────

    [Fact]
    public void Format_PrintsRefLineRightUnderThePaymentMethod_InAscii()
    {
        var texts = Texts(ReceiptFormatter.Format(Sale()));

        var medio = texts.IndexOf("Medio: Tarjeta");
        medio.Should().BeGreaterThan(0);
        texts[medio + 1].Should().Be(PrintedRef, "the middle dot becomes a dash, never '?'");
        texts.Should().OnlyContain(t => System.Text.RegularExpressions.Regex.IsMatch(t, "^[\\x20-\\x7E]*$"));
    }

    [Fact]
    public void Format_PrintsVoucherLinesAfterTheSeparator_EachWithinWidth()
    {
        var voucher = new List<string>
        {
            "LOQZEN POS", "VENTA", "TOTAL          12,500 COP", "TARJETA   VISA ****4242", "APROBADA", "AUTORIZACION 123456",
            new string('X', 40),                       // longer than the receipt: clipped, never wraps/overflows
            "Autorización Ñandú"                       // non-ASCII: folded
        };
        var lines = ReceiptFormatter.Format(Sale(voucher: voucher));
        var texts = Texts(lines);

        var sep = new string('=', ReceiptFormatter.DefaultWidth);
        var refIdx = texts.IndexOf(PrintedRef);
        texts[refIdx + 1].Should().Be(sep, "the payment block closes with a separator");
        texts.Skip(refIdx + 2).Take(voucher.Count).Should().Equal(
            "LOQZEN POS", "VENTA", "TOTAL          12,500 COP", "TARJETA   VISA ****4242", "APROBADA", "AUTORIZACION 123456",
            new string('X', 32), "Autorizacion Nandu");
        foreach (var l in lines)
        {
            var max = l.IsLarge ? ReceiptFormatter.DefaultWidth / 2 : ReceiptFormatter.DefaultWidth;
            l.Text.Length.Should().BeLessThanOrEqualTo(max, $"line '{l.Text}'");
        }
        texts.Last().Should().Be("Gracias por su compra!", "the voucher goes before the footer");
    }

    [Fact]
    public void Format_VoucherHonoursCustomWidth()
    {
        var lines = ReceiptFormatter.Format(Sale(voucher: new List<string> { new string('V', 60) }), width: 42);
        lines.Should().Contain(l => l.Text == new string('V', 42));
    }

    [Fact]
    public void Format_NoReference_NoVoucher_PrintsNothingExtra()
    {
        var texts = Texts(ReceiptFormatter.Format(Sale(reference: null, voucher: null)));

        texts.Should().NotContain(t => t.StartsWith("Ref:"));
        var medio = texts.IndexOf("Medio: Tarjeta");
        texts[medio + 1].Should().Be(new string('=', ReceiptFormatter.DefaultWidth));
        texts[medio + 2].Should().Be(string.Empty);
        texts[medio + 3].Should().Be("Gracias por su compra!");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_BlankReference_IsNotPrinted(string reference)
    {
        Texts(ReceiptFormatter.Format(Sale(reference: reference))).Should().NotContain(t => t.StartsWith("Ref:"));
    }

    [Fact]
    public void Format_EmptyVoucherList_PrintsNothing()
    {
        var with = Texts(ReceiptFormatter.Format(Sale(voucher: new List<string>())));
        var without = Texts(ReceiptFormatter.Format(Sale(voucher: null)));
        with.Should().Equal(without);
    }

    [Fact]
    public void Format_Preview_HasNoPaymentBlock_NoRef_NoVoucher()
    {
        var texts = Texts(ReceiptFormatter.Format(Sale(voucher: new List<string> { "LOQZEN POS" }), isPreview: true));

        texts.Should().NotContain(t => t.StartsWith("Ref:"));
        texts.Should().NotContain("LOQZEN POS");
        texts.Should().NotContain(t => t.StartsWith("Medio:"));
    }

    [Fact]
    public void Format_LongReference_IsClippedToWidth()
    {
        var texts = Texts(ReceiptFormatter.Format(Sale(reference: "AUTH 123456 · MASTERCARD ****4242 · RRN 0123456789")));
        var line = texts.Single(t => t.StartsWith("Ref:"));
        line.Length.Should().Be(ReceiptFormatter.DefaultWidth);
        line.Should().StartWith("Ref: AUTH 123456 - MASTERCARD");
    }

    [Fact]
    public void Format_PlainText_KeepsRefAndVoucher()
    {
        var text = ReceiptFormatter.ToPlainText(ReceiptFormatter.Format(Sale(voucher: new List<string> { "APROBADA" })));
        text.Should().Contain(PrintedRef + "\n").And.Contain("APROBADA\n");
    }

    [Theory]
    [InlineData("AUTH 123456 · VISA ****4242", "AUTH 123456 - VISA ****4242")]
    [InlineData("  AUTH 1 · Ñ  ", "AUTH 1 - N")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Reference_FoldsToAscii_AndTrims(string? input, string expected)
    {
        ReceiptFormatter.Reference(input).Should().Be(expected);
    }

    // ── CashReports ──────────────────────────────────────────────────────────

    private static BillPaymentDto Recaudo(PaymentMethod method, string? reference) => new()
    {
        Id = Guid.NewGuid(), ReceiptNumber = "REC-000001", BillerName = "Energía", AccountReference = "CTA-1", Amount = 50000m, Commission = 1500m,
        Method = method, TenderedAmount = method == PaymentMethod.Cash ? 60000m : 0m, ChangeAmount = method == PaymentMethod.Cash ? 8500m : 0m,
        PaymentReference = reference, Status = BillPaymentStatus.Completed, UserName = "Ana", PaidAt = new DateTime(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void BillPaymentReceipt_PrintsRefUnderMedio_ForCard()
    {
        var texts = Texts(CashReports.BillPaymentReceipt(Recaudo(PaymentMethod.Card, Ref), "Tienda"));

        var medio = texts.FindIndex(t => t.StartsWith("Medio:"));
        medio.Should().BeGreaterThan(0);
        texts[medio].Should().Be("Medio: Tarjeta");
        texts[medio + 1].Should().Be(PrintedRef);
        texts.Should().NotContain(t => t.StartsWith("Recibido:"), "no change block for cards");
    }

    [Fact]
    public void BillPaymentReceipt_NoReference_NoRefLine()
    {
        Texts(CashReports.BillPaymentReceipt(Recaudo(PaymentMethod.Card, null), "Tienda")).Should().NotContain(t => t.StartsWith("Ref:"));
        Texts(CashReports.BillPaymentReceipt(Recaudo(PaymentMethod.Cash, null), "Tienda")).Should().NotContain(t => t.StartsWith("Ref:"));
    }

    private static ShiftReconciliationDto Shift(decimal terminalTotal, int terminalCount) => new()
    {
        ShiftId = Guid.NewGuid(), ShiftNumber = 3, CashierName = "Ana", Status = CashShiftStatus.Closed,
        OpenedAt = new DateTime(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc), ClosedAt = new DateTime(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc),
        OpeningAmount = 100000m, CashSales = 200000m, CardSales = 150000m, TransferSales = 0m,
        TerminalCardTotal = terminalTotal, TerminalChargeCount = terminalCount, CountedCash = 300000m
    };

    [Fact]
    public void ZReport_PrintsPorDatafono_UnderTarjeta_WhenTerminalTotalIsPositive()
    {
        var texts = Texts(CashReports.ZReport(Shift(120000m, 7), "Tienda"));

        var tarjeta = texts.FindIndex(t => t.StartsWith("Tarjeta:"));
        tarjeta.Should().BeGreaterThan(0);
        texts[tarjeta + 1].Should().StartWith("  por datafono:").And.EndWith("$120.000 (7)");
        texts[tarjeta + 1].Length.Should().BeLessThanOrEqualTo(ReceiptFormatter.DefaultWidth);
    }

    [Fact]
    public void ZReport_OmitsPorDatafono_WhenZero()
    {
        Texts(CashReports.ZReport(Shift(0m, 0), "Tienda")).Should().NotContain(t => t.Contains("datafono"));
    }

    // ── ReceiptDtoFactory ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void VoucherLinesFromText_Blank_IsNull(string? text)
    {
        ReceiptDtoFactory.VoucherLinesFromText(text).Should().BeNull();
    }

    [Fact]
    public void VoucherLinesFromText_SplitsOnNewline_AndStripsCarriageReturns()
    {
        ReceiptDtoFactory.VoucherLinesFromText("LOQZEN POS\nVENTA\r\nTOTAL 12,500").Should().Equal("LOQZEN POS", "VENTA", "TOTAL 12,500");
    }

    [Fact]
    public void VoucherLinesFromText_KeepsBlankLinesInsideTheVoucher()
    {
        ReceiptDtoFactory.VoucherLinesFromText("A\n\nB").Should().Equal("A", "", "B");
    }

    private static OrderDto Order(string? reference) => new()
    {
        Id = Guid.NewGuid(), OrderNumber = "FAC-9", Subtotal = 5000, TaxTotal = 950, Total = 5950, PaymentReference = reference,
        Items = { new OrderItemDto { ProductName = "Gaseosa", Quantity = 1, UnitPrice = 5000, Total = 5950 } }
    };

    [Fact]
    public void FromOrder_Card_UsesPersistedReference_UnlessCallerOverrides()
    {
        var fromDb = ReceiptDtoFactory.FromOrder(Order(Ref), "Ana", null, PaymentMethod.Card, 0, 0);
        fromDb.PaymentReference.Should().Be(Ref);
        fromDb.PaymentMethodName.Should().Be("Tarjeta");
        fromDb.TenderedAmount.Should().Be(0);
        fromDb.VoucherLines.Should().BeNull();

        var overridden = ReceiptDtoFactory.FromOrder(Order(Ref), "Ana", null, PaymentMethod.Card, 0, 0, " AUTH 1 · AMEX ****0005 ", new[] { "V1", "V2" });
        overridden.PaymentReference.Should().Be("AUTH 1 · AMEX ****0005");
        overridden.VoucherLines.Should().Equal("V1", "V2");

        ReceiptDtoFactory.FromOrder(Order("  "), "Ana", null, PaymentMethod.Transfer, 0, 0).PaymentReference.Should().BeNull();
    }

    [Fact]
    public void FromOrder_Cash_NeverPrintsAReference_AndEmptyVoucherIsNull()
    {
        var dto = ReceiptDtoFactory.FromOrder(Order(Ref), "Ana", null, PaymentMethod.Cash, 10000, 4050, Ref, Array.Empty<string>());
        dto.PaymentReference.Should().BeNull();
        dto.VoucherLines.Should().BeNull();
        dto.TenderedAmount.Should().Be(10000);
        dto.ChangeAmount.Should().Be(4050);
    }

    [Fact]
    public void TerminalPaymentDto_ReferenceText_Shape()
    {
        new TerminalPaymentDto { AuthorizationCode = "123456", CardBrand = "VISA", CardLast4 = "4242" }.ReferenceText.Should().Be(Ref);
        new TerminalPaymentDto { AuthorizationCode = "123456" }.ReferenceText.Should().Be("AUTH 123456");
        new TerminalPaymentDto { AuthorizationCode = "123456", CardLast4 = "4242" }.ReferenceText.Should().Be("AUTH 123456 ****4242");
        new TerminalPaymentDto { CardBrand = "VISA", CardLast4 = "4242" }.ReferenceText.Should().BeEmpty("no approval, no reference");
        ReceiptFormatter.Reference(new TerminalPaymentDto { AuthorizationCode = "123456", CardBrand = "VISA", CardLast4 = "4242" }.ReferenceText)
            .Should().Be("AUTH 123456 - VISA ****4242");
    }
}
