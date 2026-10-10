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

        try
        {
            var bytes = await files.ReadAsync(doc, ct);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var result = await reader.ReadAsync(bytes, doc.ContentType, useStrongModel: false, ct);
            var warnings = InvoiceChecks.Run(result.Invoice, await IsDuplicateAsync(db, doc, result.Invoice, ct), today);

            // Second opinion from the stronger model when the cheap read looks wrong.
            if (reader.HasStrongModel && InvoiceChecks.NeedsSecondOpinion(warnings))
            {
                var second = await reader.ReadAsync(bytes, doc.ContentType, useStrongModel: true, ct);
                var secondWarnings = InvoiceChecks.Run(second.Invoice, await IsDuplicateAsync(db, doc, second.Invoice, ct), today);
                if (secondWarnings.Count < warnings.Count)
                {
                    result = second;
                    warnings = secondWarnings;
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

            var rule = await AccountRules.FindAsync(db, doc.OfficeId, doc.ClientId, inv.SupplierTaxId, ct);
            if (rule is not null)
            {
                data.AccountCode = rule.AccountCode;
                data.AccountSuggested = true;
            }

            db.Invoices.Add(data);
            doc.Status = DocumentStatus.NeedsReview;
            doc.Error = null;
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Read document {Id} with {Model}, {Count} warnings", doc.Id, result.ModelUsed, warnings.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Reading document {Id} failed", doc.Id);
            doc.Status = DocumentStatus.Failed;
            doc.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return true;
    }

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
