using InvoiceReader.Web.Services;

namespace InvoiceReader.Tests;

public class AiJsonTests
{
    [Fact]
    public void Parses_clean_json()
    {
        var inv = AiJson.Parse("""
            {"supplier_name":"Telekom Demo d.o.o.","supplier_tax_id":"4200000002023","supplier_vat_number":"200000002023",
             "invoice_number":"TD-2026-55871","invoice_date":"2026-10-01","net_amount":59.83,"vat_amount":10.17,
             "total_amount":70.00,"currency":"KM","confidence":0.95}
            """);

        Assert.Equal("Telekom Demo d.o.o.", inv.SupplierName);
        Assert.Equal("4200000002023", inv.SupplierTaxId);
        Assert.Equal(new DateOnly(2026, 10, 1), inv.InvoiceDate);
        Assert.Equal(70.00m, inv.TotalAmount);
        Assert.Equal("BAM", inv.Currency);
        Assert.Equal(0.95, inv.Confidence!.Value, 3);
    }

    [Fact]
    public void Tolerates_prose_fences_strings_and_decimal_commas()
    {
        var inv = AiJson.Parse("""
            Here is the data:
            ```json
            {"supplier_name":"X","supplier_tax_id":"4200-0000-0101-9","invoice_date":"30.09.2026",
             "net_amount":"1.234,50","vat_amount":"209,87","total_amount":"1.444,37","currency":null}
            ```
            """);

        Assert.Equal("4200000001019", inv.SupplierTaxId);
        Assert.Equal(new DateOnly(2026, 9, 30), inv.InvoiceDate);
        Assert.Equal(1234.50m, inv.NetAmount);
        Assert.Equal(209.87m, inv.VatAmount);
        Assert.Equal(1444.37m, inv.TotalAmount);
        Assert.Equal("BAM", inv.Currency);
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("234,00", 234.00)]
    [InlineData("234.00 KM", 234.00)]
    public void Parses_amounts_in_both_formats(string input, double expected)
    {
        Assert.Equal((decimal)expected, AiJson.ParseAmount(input));
    }

    [Fact]
    public void Missing_json_throws()
    {
        Assert.Throws<FormatException>(() => AiJson.Parse("I could not read this invoice."));
    }
}
