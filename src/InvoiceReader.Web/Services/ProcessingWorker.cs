using InvoiceReader.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Services;

/// <summary>
/// Background job: picks up uploaded documents, sends them to the AI, runs the checks,
/// fills in the account from learned supplier rules, and marks them ready for review.
/// </summary>
public class ProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ProcessingWorker> _log;

    public ProcessingWorker(IServiceScopeFactory scopes, ILogger<ProcessingWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Documents left half-read by a restart go back in the queue.
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Documents.Where(d => d.Status == DocumentStatus.Reading)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DocumentStatus.Uploaded), stop);
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                var didWork = await ProcessNextAsync(stop);
                if (!didWork) await Task.Delay(TimeSpan.FromSeconds(2), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Processing loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stop);
            }
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IInvoiceReader>();
        var files = scope.ServiceProvider.GetRequiredService<FileStore>();

        var doc = await db.Documents
            .Where(d => d.Status == DocumentStatus.Uploaded)
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(ct);
        if (doc is null) return false;

        doc.Status = DocumentStatus.Reading;
        await db.SaveChangesAsync(ct);

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var bytes = await files.ReadAsync(doc, ct);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            ReadResult result;
            List<string> warnings;
            string? earlierAccount = null;

            // The same file was already confirmed for this client: reuse that data, no AI call.
            var earlier = await FindSameFileAsync(db, doc, ct);
            if (earlier is not null)
            {
                result = new ReadResult(ToAiInvoice(earlier), earlier.RawJson, "ponovljena datoteka",
                    $"Ista datoteka je već potvrđena (dokument {earlier.DocumentId}): podaci su preuzeti bez AI čitanja.");
                warnings = InvoiceChecks.Run(result.Invoice, await IsDuplicateAsync(db, doc, result.Invoice, ct), today);
                earlierAccount = earlier.AccountCode;
            }
            else
            {
                var known = await KnownSuppliers.LoadAsync(db, doc.OfficeId, ct);
                (result, warnings) = await ReadAndCheckAsync(reader, db, doc, bytes, known, useStrongModel: false, today, ct);

                // Second opinion from the stronger model when the cheap read looks wrong.
                if (reader.HasStrongModel && InvoiceChecks.NeedsSecondOpinion(warnings))
                {
                    var (second, secondWarnings) = await ReadAndCheckAsync(reader, db, doc, bytes, known, useStrongModel: true, today, ct);
                    if (secondWarnings.Count < warnings.Count)
                    {
                        result = second;
                        warnings = secondWarnings;
                    }
                }
            }

            if (result.Note is not null) warnings.Insert(0, result.Note);

            var inv = result.Invoice;
            var data = new InvoiceData
            {
                DocumentId = doc.Id,
                SupplierName = inv.SupplierName,
                SupplierTaxId = inv.SupplierTaxId,
                SupplierVatNumber = inv.SupplierVatNumber,
                InvoiceNumber = inv.InvoiceNumber,
                InvoiceDate = inv.InvoiceDate,
                NetAmount = inv.NetAmount,
                VatAmount = inv.VatAmount,
                TotalAmount = inv.TotalAmount,
                Currency = inv.Currency,
                Confidence = inv.Confidence,
                ModelUsed = result.ModelUsed,
                RawJson = result.RawJson,
                Warnings = string.Join('\n', warnings)
            };

            var account = earlierAccount ??
                          (await AccountRules.FindAsync(db, doc.OfficeId, doc.ClientId, inv.SupplierTaxId, ct))?.AccountCode;
            if (account is not null)
            {
                data.AccountCode = account;
                data.AccountSuggested = true;
            }

            db.Invoices.Add(data);
            doc.Status = DocumentStatus.NeedsReview;
            doc.Error = null;
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Read document {Id} ({Kb} KB) with {Model} in {Seconds:0.0} s, {Count} warnings",
                doc.Id, bytes.Length / 1024, result.ModelUsed, started.Elapsed.TotalSeconds, warnings.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Reading document {Id} failed after {Seconds:0.0} s", doc.Id, started.Elapsed.TotalSeconds);
            doc.Status = DocumentStatus.Failed;
            doc.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return true;
    }

    /// <summary>One AI read, ID derived from the VAT number if needed, matched to known suppliers, then checked.</summary>
    private static async Task<(ReadResult, List<string>)> ReadAndCheckAsync(IInvoiceReader reader, AppDbContext db, Document doc,
        byte[] bytes, IReadOnlyList<KnownSupplier> known, bool useStrongModel, DateOnly today, CancellationToken ct)
    {
        var read = await reader.ReadAsync(bytes, doc.ContentType, useStrongModel, ct);
        var (withId, idNote) = TaxIds.Reconcile(read.Invoice);
        var (inv, supplierNote) = KnownSuppliers.Apply(withId, known);
        var warnings = InvoiceChecks.Run(inv, await IsDuplicateAsync(db, doc, inv, ct), today);
        if (supplierNote is not null) warnings.Insert(0, supplierNote);
        if (idNote is not null) warnings.Insert(0, idNote);
        return (read with { Invoice = inv }, warnings);
    }

    /// <summary>A confirmed invoice of the same client with exactly the same file (SHA-256).</summary>
    private static Task<InvoiceData?> FindSameFileAsync(AppDbContext db, Document doc, CancellationToken ct) =>
        db.Invoices
            .Where(i => i.Document!.ClientId == doc.ClientId &&
                        i.DocumentId != doc.Id &&
                        i.Document.Sha256 == doc.Sha256 &&
                        (i.Document.Status == DocumentStatus.Confirmed || i.Document.Status == DocumentStatus.Exported))
            .OrderByDescending(i => i.ConfirmedAt)
            .FirstOrDefaultAsync(ct);

    private static AiInvoice ToAiInvoice(InvoiceData i) => new()
    {
        SupplierName = i.SupplierName,
        SupplierTaxId = i.SupplierTaxId,
        SupplierVatNumber = i.SupplierVatNumber,
        InvoiceNumber = i.InvoiceNumber,
        InvoiceDate = i.InvoiceDate,
        NetAmount = i.NetAmount,
        VatAmount = i.VatAmount,
        TotalAmount = i.TotalAmount,
        Currency = i.Currency,
        Confidence = 1
    };

    private static Task<bool> IsDuplicateAsync(AppDbContext db, Document doc, AiInvoice inv, CancellationToken ct)
    {
        if (inv.SupplierTaxId is null || inv.InvoiceNumber is null) return Task.FromResult(false);
        return db.Invoices.AnyAsync(i =>
            i.Document!.OfficeId == doc.OfficeId &&
            i.Document.ClientId == doc.ClientId &&
            i.DocumentId != doc.Id &&
            i.SupplierTaxId == inv.SupplierTaxId &&
            i.InvoiceNumber == inv.InvoiceNumber, ct);
    }
}

/// <summary>The learning part: which supplier goes to which account.</summary>
public static class AccountRules
{
    /// <summary>Rule for this client first; otherwise the most used rule for the supplier in this office.</summary>
    public static async Task<SupplierRule?> FindAsync(AppDbContext db, int officeId, int clientId, string? supplierTaxId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(supplierTaxId)) return null;

        return await db.SupplierRules
                   .FirstOrDefaultAsync(r => r.OfficeId == officeId && r.ClientId == clientId && r.SupplierTaxId == supplierTaxId, ct)
               ?? await db.SupplierRules
                   .Where(r => r.OfficeId == officeId && r.SupplierTaxId == supplierTaxId)
                   .OrderByDescending(r => r.TimesUsed)
                   .FirstOrDefaultAsync(ct);
    }

    /// <summary>Called when the accountant confirms an invoice: remember the account for next time.</summary>
    public static async Task LearnAsync(AppDbContext db, int officeId, int clientId, string? supplierTaxId, string? accountCode)
    {
        if (string.IsNullOrEmpty(supplierTaxId) || string.IsNullOrEmpty(accountCode)) return;

        var rule = await db.SupplierRules.FirstOrDefaultAsync(r =>
            r.OfficeId == officeId && r.ClientId == clientId && r.SupplierTaxId == supplierTaxId);

        if (rule is null)
        {
            db.SupplierRules.Add(new SupplierRule
            {
                OfficeId = officeId,
                ClientId = clientId,
                SupplierTaxId = supplierTaxId,
                AccountCode = accountCode,
                TimesUsed = 1
            });
        }
        else
        {
            if (rule.AccountCode != accountCode)
            {
                rule.AccountCode = accountCode;
                rule.TimesUsed = 1;
            }
            else
            {
                rule.TimesUsed++;
            }
            rule.UpdatedAt = DateTime.UtcNow;
        }
    }
}
