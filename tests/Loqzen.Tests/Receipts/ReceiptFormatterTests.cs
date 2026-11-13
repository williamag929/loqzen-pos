using Core.Application.DTOs;
using Core.Application.Receipts;
using FluentAssertions;

namespace Loqzen.Tests.Receipts;

public class ReceiptFormatterTests
{
    private static ReceiptDto Sample() => new()
    {
        StoreName = "Café Ñandú",
        StoreAddress = "Calle 1",
        StoreTaxId = "NIT 1",
        OrderNumber = "FAC-1",
        CashierName = "Ana",
        Date = new DateTime(2026, 11, 12, 10, 0, 0),
        Items =
        {
            new ReceiptItemDto { Name = "Hamburguesa Especial Doble Carne", Quantity = 2, UnitPrice = 22000, Total = 52360 },
            new ReceiptItemDto { Name = "Gaseosa", Quantity = 1, UnitPrice = 5000, Total = 5950, IsReturn = true }
        },
        Subtotal = 49000, TaxTotal = 9310, Total = 58310,
        PaymentMethodName = "Cash", TenderedAmount = 60000, ChangeAmount = 1690
    };

    [Fact]
    public void AllLines_FitWidth_AndAreAscii()
    {
        var lines = ReceiptFormatter.Format(Sample());
        foreach (var l in lines)
        {
            var max = l.IsLarge ? ReceiptFormatter.DefaultWidth / 2 : ReceiptFormatter.DefaultWidth;
            l.Text.Length.Should().BeLessThanOrEqualTo(max, $"line '{l.Text}'");
            l.Text.Should().MatchRegex("^[\\x20-\\x7E]*$", "printer encodes ASCII");
        }
        lines.Should().Contain(l => l.Text.Contains("Cafe Nandu"), "accents are stripped, not mangled");
    }

    [Fact]
    public void LongItemName_IsTruncated_ButTotalIsKept()
    {
        var line = ReceiptFormatter.Format(Sample()).First(l => l.Text.Contains("Hamburguesa"));
        line.Text.Should().EndWith("$52.360");
    }

    [Fact]
    public void Returns_PrintedInCreditNoteSection()
    {
        var text = ReceiptFormatter.ToPlainText(ReceiptFormatter.Format(Sample()));
        var credit = text.IndexOf("*** NOTA CREDITO ***", StringComparison.Ordinal);
        credit.Should().BeGreaterThan(0);
        text.IndexOf("DEVOL: Gaseosa", StringComparison.Ordinal).Should().BeGreaterThan(credit);
    }

    [Fact]
    public void Preview_HasPreCuentaMarker_AndNoPaymentBlock()
    {
        var text = ReceiptFormatter.ToPlainText(ReceiptFormatter.Format(Sample(), isPreview: true));
        text.Should().Contain("PRE-CUENTA");
        text.Should().NotContain("Recibido:");
    }
}
