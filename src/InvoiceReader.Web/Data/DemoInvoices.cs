using System.Text.Json;
using InvoiceReader.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Data;

/// <summary>
/// Gives two of the demo clients some history, so the dashboard, export history and learned
/// accounts are not empty on a fresh start. Primjer Market stays empty for the live demo
/// (README "Demo scenario"), and no rule is seeded for the Energo supplier, because rules
/// also apply office-wide and step 4 of the scenario shows Energo as a new supplier.
/// </summary>
public static class DemoInvoices
{
    private const string Pekara = "Demo Pekara d.o.o.";
    private const string TestServis = "Test Servis s.p.";

    // Sample file -> cost account the accountant "picked".
    private static readonly (string File, string Account)[] Booked =
    {
        ("03_telekom_internet.png", "5310"),
        ("04_gorivo.jpg", "5121"),
        ("05_kancelarija_plus.png", "5100")
    };

    public static async Task SeedAsync(AppDbContext db, FileStore files, string contentRoot)
    {
        var clients = await db.Clients
            .Where(c => c.OfficeId == SeedData.DemoOfficeId && (c.Name == Pekara || c.Name == TestServis))
            .ToListAsync();
        var pekara = clients.FirstOrDefault(c => c.Name == Pekara);
        var testServis = clients.FirstOrDefault(c => c.Name == TestServis);
        if (pekara is null || testServis is null) return;
        if (await db.Documents.AnyAsync(d => d.ClientId == pekara.Id || d.ClientId == testServis.Id)) return;

        var dir = Path.Combine(contentRoot, "SampleInvoices");
        var samplesPath = Path.Combine(dir, "samples.json");
        if (!File.Exists(samplesPath)) return;

        using var samplesJson = JsonDocument.Parse(await File.ReadAllTextAsync(samplesPath));
        var answers = samplesJson.RootElement.EnumerateArray().ToDictionary(
            s => s.GetProperty("file").GetString()!,
            s => s.GetProperty("answer").GetRawText());

        // Demo Pekara: three invoices confirmed and ready for the October export.
        foreach (var (file, account) in Booked)
            await AddBookedAsync(db, files, dir, pekara, file, answers[file], account, DocumentStatus.Confirmed);

        // Test Servis: the same three already exported for October.
        var period = "";
        foreach (var (file, account) in Booked)
        {
            var inv = await AddBookedAsync(db, files, dir, testServis, file, answers[file], account, DocumentStatus.Exported);
            period = inv.InvoiceDate?.ToString("yyyy-MM") ?? period;
        }
        db.Exports.Add(new ExportRecord
        {
            OfficeId = SeedData.DemoOfficeId,
            ClientId = testServis.Id,
            Period = period,
            FileName = $"Test_Servis_s_p_{period}.csv",
            InvoiceCount = Booked.Length
        });

        // One fresh upload each; the background worker reads them like any other upload.
        await AddUploadAsync(db, files, dir, pekara, "06_servis_klima.png");
        await AddUploadAsync(db, files, dir, testServis, "02_energo_oktobar.png");

        await db.SaveChangesAsync();
    }

    private static async Task<InvoiceData> AddBookedAsync(AppDbContext db, FileStore files, string dir, Client client,
        string file, string answerJson, string account, DocumentStatus status)
    {
        var doc = await SaveFileAsync(files, dir, client, file);
        doc.Status = status;

        var read = AiJson.Parse(answerJson);
        var inv = new InvoiceData
        {
            Document = doc,
            SupplierName = read.SupplierName,
            SupplierTaxId = read.SupplierTaxId,
            SupplierVatNumber = read.SupplierVatNumber,
            InvoiceNumber = read.InvoiceNumber,
            InvoiceDate = read.InvoiceDate,
            NetAmount = read.NetAmount,
            VatAmount = read.VatAmount,
            TotalAmount = read.TotalAmount,
            Currency = read.Currency,
            Confidence = read.Confidence,
            ModelUsed = "demo",
            RawJson = answerJson,
            AccountCode = account,
            ConfirmedAt = DateTime.UtcNow
        };
        db.Documents.Add(doc);
        db.Invoices.Add(inv);

        db.SupplierRules.Add(new SupplierRule
        {
            OfficeId = client.OfficeId,
            ClientId = client.Id,
            SupplierTaxId = read.SupplierTaxId!,
            AccountCode = account,
            TimesUsed = 1
        });
        return inv;
    }

    private static async Task AddUploadAsync(AppDbContext db, FileStore files, string dir, Client client, string file) =>
        db.Documents.Add(await SaveFileAsync(files, dir, client, file));

    private static async Task<Document> SaveFileAsync(FileStore files, string dir, Client client, string file)
    {
        await using var s = File.OpenRead(Path.Combine(dir, file));
        var (doc, error) = await files.SaveAsync(s, file, s.Length, client.OfficeId, client.Id);
        return doc ?? throw new InvalidOperationException(error);
    }
}
