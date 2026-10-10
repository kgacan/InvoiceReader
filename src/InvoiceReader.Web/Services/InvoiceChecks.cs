namespace InvoiceReader.Web.Services;

/// <summary>
/// Plain-code checks run after the AI reads an invoice. They catch most reading mistakes
/// before the accountant sees them. No AI involved.
/// </summary>
public static class InvoiceChecks
{
    /// <summary>Standard VAT rate in Bosnia and Herzegovina.</summary>
    public const decimal VatRate = 0.17m;

    public static List<string> Run(AiInvoice inv, bool isDuplicate, DateOnly today)
    {
        var w = new List<string>();

        if (string.IsNullOrWhiteSpace(inv.SupplierName)) w.Add("Nedostaje naziv dobavljača.");
        if (string.IsNullOrWhiteSpace(inv.InvoiceNumber)) w.Add("Nedostaje broj računa.");
        if (inv.InvoiceDate is null) w.Add("Nedostaje datum računa.");
        if (inv.TotalAmount is null) w.Add("Nedostaje ukupan iznos.");

        if (inv.SupplierTaxId is null)
            w.Add("Nedostaje ID broj (JIB) dobavljača.");
        else if (inv.SupplierTaxId.Length != 13)
            w.Add($"ID broj dobavljača ima {inv.SupplierTaxId.Length} cifara, očekivano 13.");

        if (inv.SupplierVatNumber is not null && inv.SupplierVatNumber.Length != 12)
            w.Add($"PDV broj dobavljača ima {inv.SupplierVatNumber.Length} cifara, očekivano 12.");

        if (inv.NetAmount is { } net && inv.VatAmount is { } vat && inv.TotalAmount is { } total)
        {
            if (Math.Abs(net + vat - total) > 0.02m)
                w.Add($"Osnovica + PDV ({CsvExporter.Amount(net + vat)}) nije jednako ukupnom iznosu ({CsvExporter.Amount(total)}).");

            if (vat == 0)
                w.Add("PDV je 0. Provjerite da li je dobavljač u sistemu PDV-a.");
            else if (Math.Abs(net * VatRate - vat) > 0.05m)
                w.Add($"PDV ({CsvExporter.Amount(vat)}) nije 17% osnovice (očekivano {CsvExporter.Amount(Math.Round(net * VatRate, 2))}).");
        }

        if (inv.InvoiceDate is { } date && date > today)
            w.Add("Datum računa je u budućnosti.");

        if (isDuplicate)
            w.Add("Račun s istim brojem od ovog dobavljača je već učitan (mogući duplikat).");

        if (inv.Confidence is < 0.7)
            w.Add("AI nije siguran u očitane podatke. Provjerite pažljivo.");

        return w;
    }

    /// <summary>Warnings worth a second read with the stronger model.</summary>
    public static bool NeedsSecondOpinion(IEnumerable<string> warnings) =>
        warnings.Any(x => x.StartsWith("Osnovica") || x.StartsWith("Nedostaje") || x.StartsWith("AI nije"));
}
