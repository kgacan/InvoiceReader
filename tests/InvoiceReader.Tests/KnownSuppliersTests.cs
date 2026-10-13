using InvoiceReader.Web.Services;

namespace InvoiceReader.Tests;

public class KnownSuppliersTests
{
    private static readonly KnownSupplier Energo = new("4200000001019", "Energo Primjer d.o.o.", "200000001019");
    private static readonly KnownSupplier Telekom = new("4200000002023", "Telekom Demo d.o.o.", "200000002023");
    private static readonly KnownSupplier[] Known = { Energo, Telekom };

    private static AiInvoice Read(string? taxId, string? name = "Energo d.o.o", string? vat = null) =>
        new() { SupplierTaxId = taxId, SupplierName = name, SupplierVatNumber = vat };

    [Fact]
    public void Exact_id_takes_confirmed_name_and_vat_without_note()
    {
        var (inv, note) = KnownSuppliers.Apply(Read("4200000001019"), Known);
        Assert.Equal("Energo Primjer d.o.o.", inv.SupplierName);
        Assert.Equal("200000001019", inv.SupplierVatNumber);
        Assert.Null(note);
    }

    [Fact]
    public void One_misread_digit_is_corrected_with_note()
    {
        var (inv, note) = KnownSuppliers.Apply(Read("4200000001079"), Known);
        Assert.Equal("4200000001019", inv.SupplierTaxId);
        Assert.Contains("4200000001079", note);
    }

    [Fact]
    public void Matching_vat_number_fixes_id()
    {
        var (inv, note) = KnownSuppliers.Apply(Read("9999999999999", vat: "200000002023"), Known);
        Assert.Equal("4200000002023", inv.SupplierTaxId);
        Assert.NotNull(note);
    }

    [Fact]
    public void Missing_id_is_taken_from_exact_name()
    {
        var (inv, note) = KnownSuppliers.Apply(Read(null, "TELEKOM demo d.o.o"), Known);
        Assert.Equal("4200000002023", inv.SupplierTaxId);
        Assert.NotNull(note);
    }

    [Fact]
    public void Ambiguous_or_unknown_supplier_is_left_alone()
    {
        // 4200000001013 is one digit from Energo and one digit from 4200000001012.
        var twoNear = new[] { Energo, new KnownSupplier("4200000001012", "Druga firma", null) };
        var ambiguous = Read("4200000001013");
        Assert.Equal((ambiguous, (string?)null), KnownSuppliers.Apply(ambiguous, twoNear));

        var unknown = Read("4200000009999", "Nova firma");
        Assert.Equal((unknown, (string?)null), KnownSuppliers.Apply(unknown, Known));
    }
}
