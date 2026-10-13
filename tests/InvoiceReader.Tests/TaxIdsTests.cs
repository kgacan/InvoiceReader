using InvoiceReader.Web.Services;

namespace InvoiceReader.Tests;

public class TaxIdsTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);

    [Fact]
    public void Invented_12_digit_id_is_replaced_from_vat_number()
    {
        // Real case: the invoice prints only the PDV broj; the AI made up a 12-digit "JIB".
        var read = new AiInvoice { SupplierTaxId = "420098900007", SupplierVatNumber = "200209890007" };
        var (inv, note) = TaxIds.Reconcile(read);
        Assert.Equal("4200209890007", inv.SupplierTaxId);
        Assert.Contains("420098900007", note);
    }

    [Fact]
    public void Missing_id_is_derived_from_vat_number()
    {
        var (inv, note) = TaxIds.Reconcile(new AiInvoice { SupplierVatNumber = "200209890007" });
        Assert.Equal("4200209890007", inv.SupplierTaxId);
        Assert.NotNull(note);
    }

    [Fact]
    public void Matching_or_well_formed_id_and_missing_vat_are_left_alone()
    {
        var matching = new AiInvoice { SupplierTaxId = "4200209890007", SupplierVatNumber = "200209890007" };
        Assert.Equal((matching, (string?)null), TaxIds.Reconcile(matching));

        var conflicting = new AiInvoice { SupplierTaxId = "4200209890008", SupplierVatNumber = "200209890007" };
        Assert.Equal((conflicting, (string?)null), TaxIds.Reconcile(conflicting));

        var noVat = new AiInvoice { SupplierTaxId = "4200209890007" };
        Assert.Equal((noVat, (string?)null), TaxIds.Reconcile(noVat));
    }

    [Fact]
    public void Checks_warn_when_id_and_vat_number_disagree()
    {
        var inv = new AiInvoice { SupplierTaxId = "4200209890008", SupplierVatNumber = "200209890007" };
        var w = InvoiceChecks.Run(inv, false, Today);
        Assert.Contains(w, x => x.StartsWith("ID broj i PDV broj se ne slažu"));
    }
}
