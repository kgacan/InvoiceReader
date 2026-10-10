using System.Globalization;
using System.Text.Json;

namespace InvoiceReader.Web.Services;

/// <summary>The fields the AI is asked to read from an invoice.</summary>
public record AiInvoice
{
    public string? SupplierName { get; init; }
    public string? SupplierTaxId { get; init; }
    public string? SupplierVatNumber { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public decimal? NetAmount { get; init; }
    public decimal? VatAmount { get; init; }
    public decimal? TotalAmount { get; init; }
    public string Currency { get; init; } = "BAM";
    public double? Confidence { get; init; }
}

/// <summary>Result of one read: the parsed fields plus what produced them.</summary>
public record ReadResult(AiInvoice Invoice, string RawJson, string ModelUsed, string? Note = null);

/// <summary>
/// Parses the JSON an AI model returns. Models sometimes wrap JSON in prose or code fences,
/// write numbers as strings, or use a decimal comma, so parsing is deliberately tolerant.
/// </summary>
public static class AiJson
{
    public const string Instruction =
        """
        You read invoices from Bosnia and Herzegovina for an accounting office.
        Look at the attached invoice and return ONLY a JSON object, no other text, with these keys:
        "supplier_name": seller's company name,
        "supplier_tax_id": seller's 13-digit ID number (ID broj / JIB), digits only,
        "supplier_vat_number": seller's 12-digit VAT number (PDV broj), digits only, or null,
        "invoice_number": invoice number exactly as printed,
        "invoice_date": invoice date as YYYY-MM-DD,
        "net_amount": amount without VAT (osnovica), as a number,
        "vat_amount": VAT amount (PDV), as a number,
        "total_amount": total to pay (ukupno), as a number,
        "currency": ISO code, use "BAM" for KM,
        "confidence": your confidence that all fields are correct, from 0 to 1.
        Use a dot as the decimal separator. Use null for anything you cannot read. Never guess numbers.
        """;

    public static AiInvoice Parse(string text)
    {
        var json = ExtractObject(text);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new AiInvoice
        {
            SupplierName = Str(root, "supplier_name"),
            SupplierTaxId = Digits(Str(root, "supplier_tax_id")),
            SupplierVatNumber = Digits(Str(root, "supplier_vat_number")),
            InvoiceNumber = Str(root, "invoice_number"),
            InvoiceDate = Date(Str(root, "invoice_date")),
            NetAmount = Num(root, "net_amount"),
            VatAmount = Num(root, "vat_amount"),
            TotalAmount = Num(root, "total_amount"),
            Currency = NormalizeCurrency(Str(root, "currency")),
            Confidence = (double?)Num(root, "confidence")
        };
    }

    /// <summary>Returns the first {...} block in the text.</summary>
    public static string ExtractObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new FormatException("The AI answer did not contain a JSON object.");
        return text.Substring(start, end - start + 1);
    }

    private static string? Str(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(el.GetString()) ? null : el.GetString()!.Trim(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null
        };
    }

    private static decimal? Num(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String) return ParseAmount(el.GetString());
        return null;
    }

    /// <summary>Parses "1.234,56", "1,234.56", "1234,56" or "1234.56".</summary>
    public static decimal? ParseAmount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => char.IsDigit(c) || c is ',' or '.' or '-').ToArray());
        if (t.Length == 0) return null;

        var lastComma = t.LastIndexOf(',');
        var lastDot = t.LastIndexOf('.');
        if (lastComma > lastDot)
            t = t.Replace(".", "").Replace(',', '.');
        else
            t = t.Replace(",", "");

        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static DateOnly? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yyyy.", "d.M.yyyy." };
        return DateOnly.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static string? Digits(string? s)
    {
        if (s is null) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }

    private static string NormalizeCurrency(string? s) =>
        s?.Trim().ToUpperInvariant() switch
        {
            null or "" or "KM" or "BAM" => "BAM",
            var other => other
        };
}
