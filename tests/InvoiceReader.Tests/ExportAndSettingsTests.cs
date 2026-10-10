using System.Text;
using InvoiceReader.Web.Data;
using InvoiceReader.Web.Pages;
using InvoiceReader.Web.Services;

namespace InvoiceReader.Tests;

public class ExportAndSettingsTests
{
    [Fact]
    public void Csv_has_header_bom_semicolons_and_decimal_commas()
    {
        var office = new Office { VatInputAccount = "2700", SuppliersAccount = "4320" };
        var invoices = new[]
        {
            new InvoiceData
            {
                SupplierName = "Firma; s tačka-zarezom", SupplierTaxId = "4200000001019", SupplierVatNumber = "200000001019",
                InvoiceNumber = "1/2026", InvoiceDate = new DateOnly(2026, 10, 7),
                NetAmount = 1234.5m, VatAmount = 209.87m, TotalAmount = 1444.37m, AccountCode = "5120"
            }
        };

        var bytes = CsvExporter.Build(invoices, office);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(';', CsvExporter.Header), lines[0]);
        Assert.Equal("07.10.2026;1/2026;\"Firma; s tačka-zarezom\";4200000001019;200000001019;1234,50;209,87;1444,37;BAM;5120;2700;4320", lines[1]);
    }

    [Fact]
    public void Accounts_paste_accepts_semicolon_tab_and_comma()
    {
        var parsed = SettingsModel.ParseAccounts("5120;Struja\n5310\tTelefon\n5330, Održavanje\n\nbez separatora\n5120;Duplikat");
        Assert.Equal(3, parsed.Count);
        Assert.Equal(("5330", "Održavanje"), parsed[2]);
    }

    [Theory]
    [InlineData("2026-10", true)]
    [InlineData("2026-13", false)]
    [InlineData("", false)]
    public void Export_period_parsing(string period, bool ok)
    {
        Assert.Equal(ok, ExportModel.TryParsePeriod(period, out var from, out var to));
        if (ok)
        {
            Assert.Equal(new DateOnly(2026, 10, 1), from);
            Assert.Equal(new DateOnly(2026, 11, 1), to);
        }
    }
}
