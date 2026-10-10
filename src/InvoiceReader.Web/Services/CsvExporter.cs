using System.Globalization;
using System.Text;
using InvoiceReader.Web.Data;

namespace InvoiceReader.Web.Services;

/// <summary>
/// Builds a generic import file: one line per invoice, semicolon-separated, decimal comma,
/// UTF-8 with BOM so Excel in the BiH locale opens it correctly.
/// A real accounting program gets its own exporter that writes its exact import format.
/// </summary>
public static class CsvExporter
{
    public static readonly string[] Header =
    {
        "Datum", "BrojRacuna", "Dobavljac", "IDBroj", "PDVBroj", "Osnovica", "PDV", "Ukupno", "Valuta",
        "KontoTroska", "KontoPDV", "KontoDobavljaca"
    };

    public static byte[] Build(IEnumerable<InvoiceData> invoices, Office office)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', Header));

        foreach (var i in invoices.OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNumber))
        {
            sb.AppendLine(string.Join(';',
                i.InvoiceDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "",
                Cell(i.InvoiceNumber),
                Cell(i.SupplierName),
                Cell(i.SupplierTaxId),
                Cell(i.SupplierVatNumber),
                Amount(i.NetAmount),
                Amount(i.VatAmount),
                Amount(i.TotalAmount),
                i.Currency,
                Cell(i.AccountCode),
                Cell(office.VatInputAccount),
                Cell(office.SuppliersAccount)));
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return preamble.Concat(body).ToArray();
    }

    /// <summary>Decimal comma without depending on OS culture data (Docker images may lack it).</summary>
    public static string Amount(decimal? v) => v?.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') ?? "";

    /// <summary>Quotes a value if it contains the separator, a quote or a line break.</summary>
    public static string Cell(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}
