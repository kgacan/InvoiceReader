using InvoiceReader.Web.Services;

namespace InvoiceReader.Tests;

public class InvoiceChecksTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);

    private static AiInvoice Valid() => new()
    {
        SupplierName = "Energo Primjer d.o.o.",
        SupplierTaxId = "4200000001019",
        SupplierVatNumber = "200000001019",
        InvoiceNumber = "0912/2026",
        InvoiceDate = new DateOnly(2026, 9, 30),
        NetAmount = 200.00m,
        VatAmount = 34.00m,
        TotalAmount = 234.00m,
        Confidence = 0.97
    };

    [Fact]
    public void Correct_invoice_has_no_warnings()
    {
        Assert.Empty(InvoiceChecks.Run(Valid(), isDuplicate: false, Today));
    }

    [Fact]
    public void Misread_total_is_caught()
    {
        var w = InvoiceChecks.Run(Valid() with { TotalAmount = 243.00m }, false, Today);
        Assert.Contains(w, x => x.StartsWith("Osnovica + PDV"));
        Assert.True(InvoiceChecks.NeedsSecondOpinion(w));
    }

    [Fact]
    public void Wrong_vat_rate_is_caught()
    {
        var w = InvoiceChecks.Run(Valid() with { VatAmount = 20.00m, TotalAmount = 220.00m }, false, Today);
        Assert.Contains("PDV (20,00) nije 17% osnovice (očekivano 34,00).", w);
    }

    [Fact]
    public void Rounding_by_a_cent_is_accepted()
    {
        var inv = Valid() with { NetAmount = 128.21m, VatAmount = 21.80m, TotalAmount = 150.01m };
        Assert.Empty(InvoiceChecks.Run(inv, false, Today));
    }

    [Theory]
    [InlineData("420000000101", "ID broj dobavljača ima 12 cifara")]
    [InlineData(null, "Nedostaje ID broj")]
    public void Tax_id_must_have_13_digits(string? taxId, string expected)
    {
        var w = InvoiceChecks.Run(Valid() with { SupplierTaxId = taxId }, false, Today);
        Assert.Contains(w, x => x.StartsWith(expected));
    }

    [Fact]
    public void Future_date_duplicate_and_low_confidence_are_flagged()
    {
        var w = InvoiceChecks.Run(Valid() with { InvoiceDate = new DateOnly(2026, 12, 1), Confidence = 0.5 }, isDuplicate: true, Today);
        Assert.Contains(w, x => x.Contains("budućnosti"));
        Assert.Contains(w, x => x.Contains("duplikat"));
        Assert.Contains(w, x => x.StartsWith("AI nije siguran"));
    }
}
