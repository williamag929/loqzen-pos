using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Core.Application.DTOs;

namespace Core.Application.Receipts;

public static partial class ReceiptFormatter
{
    private static void AddItem(List<ReceiptLine> lines, ReceiptItemDto item, string name, int width)
    {
        // "qty name.......... total" — total is never truncated, name absorbs the slack.
        var qty = item.Quantity.ToString("0.##", CultureInfo.InvariantCulture).PadLeft(4);
        var total = Cop(item.Total);
        var nameWidth = Math.Max(1, width - qty.Length - 1 - total.Length - 1);
        var n = Ascii(name);
        n = n.Length > nameWidth ? n[..nameWidth] : n.PadRight(nameWidth);
        lines.Add(new ReceiptLine($"{qty} {n} {total}"));
        if (!string.IsNullOrWhiteSpace(item.Notes))
            lines.Add(new ReceiptLine(Fit($"     ({item.Notes})", width)));
    }

    /// <summary>Colombian peso format: $12.345 (negative: -$3.000).</summary>
    public static string Cop(decimal amount) =>
        (amount < 0 ? "-$" : "$") + Math.Abs(amount).ToString("N0", Money);

    private static string Columns(string left, string right, int width)
    {
        left = Ascii(left);
        right = Ascii(right);
        var space = width - left.Length - right.Length;
        if (space < 1)
        {
            left = left[..Math.Max(0, width - right.Length - 1)];
            space = 1;
        }
        return left + new string(' ', space) + right;
    }

    private static string Fit(string text, int width)
    {
        var t = Ascii(text);
        return t.Length > width ? t[..width] : t;
    }

    private static string Center(string text, int width) =>
        text.Length >= width ? text : new string(' ', (width - text.Length) / 2) + text;

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var t = Ascii(text);
        for (var i = 0; i < t.Length; i += width)
            yield return t.Substring(i, Math.Min(width, t.Length - i));
    }

    /// <summary>Strips diacritics (á→a, ñ→n) and replaces other non-ASCII with '?', matching the ASCII printer.</summary>
    public static string Ascii(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c < 128 ? c : '?');
        }
        return sb.ToString();
    }
}
