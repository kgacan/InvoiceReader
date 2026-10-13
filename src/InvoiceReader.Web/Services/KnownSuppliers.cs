using InvoiceReader.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Services;

/// <summary>A supplier as the accountant last confirmed it.</summary>
public record KnownSupplier(string TaxId, string? Name, string? VatNumber);

/// <summary>
/// The office's supplier list, built from confirmed invoices (they hold the accountant's corrections).
/// After each AI read the supplier is matched against it, so a corrected name or a misread digit
/// in the ID does not have to be fixed again on the next invoice.
/// </summary>
public static class KnownSuppliers
{
    /// <summary>Latest confirmed details for every supplier tax ID in the office.</summary>
    public static async Task<List<KnownSupplier>> LoadAsync(AppDbContext db, int officeId, CancellationToken ct)
    {
        var rows = await db.Invoices
            .Where(i => i.Document!.OfficeId == officeId &&
                        (i.Document.Status == DocumentStatus.Confirmed || i.Document.Status == DocumentStatus.Exported) &&
                        i.SupplierTaxId != null)
            .OrderByDescending(i => i.ConfirmedAt)
            .Select(i => new KnownSupplier(i.SupplierTaxId!, i.SupplierName, i.SupplierVatNumber))
            .ToListAsync(ct);

        return rows.GroupBy(s => s.TaxId).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Fills in the supplier from the list. Returns the (possibly) changed invoice and a note for the
    /// accountant when an ID was corrected. Guesses only when exactly one known supplier fits.
    /// </summary>
    public static (AiInvoice Invoice, string? Note) Apply(AiInvoice inv, IReadOnlyList<KnownSupplier> known)
    {
        if (known.Count == 0) return (inv, null);

        var exact = inv.SupplierTaxId is null ? null : known.FirstOrDefault(k => k.TaxId == inv.SupplierTaxId);
        if (exact is not null) return (WithSupplier(inv, exact), null);

        // Same VAT number, or an ID that differs from a known one in a single digit.
        var near = known.Where(k =>
                (inv.SupplierVatNumber is not null && k.VatNumber == inv.SupplierVatNumber) ||
                (inv.SupplierTaxId is not null && OneDigitApart(inv.SupplierTaxId, k.TaxId)))
            .ToList();
        if (near.Count == 1)
        {
            var k = near[0];
            var read = inv.SupplierTaxId ?? "—";
            return (WithSupplier(inv, k),
                $"ID broj dobavljača pročitan kao {read}, ispravljen na {k.TaxId} ({k.Name}) prema ranije potvrđenim fakturama. Provjerite.");
        }

        // No usable ID: fall back to the exact supplier name.
        if (inv.SupplierTaxId is null or { Length: not 13 } && !string.IsNullOrWhiteSpace(inv.SupplierName))
        {
            var byName = known.Where(k => SameName(k.Name, inv.SupplierName)).ToList();
            if (byName.Count == 1)
                return (WithSupplier(inv, byName[0]),
                    $"ID broj dobavljača nije očitan; preuzet {byName[0].TaxId} prema nazivu iz ranije potvrđenih faktura. Provjerite.");
        }

        return (inv, null);
    }

    private static AiInvoice WithSupplier(AiInvoice inv, KnownSupplier k) => inv with
    {
        SupplierTaxId = k.TaxId,
        SupplierName = string.IsNullOrWhiteSpace(k.Name) ? inv.SupplierName : k.Name,
        SupplierVatNumber = k.VatNumber ?? inv.SupplierVatNumber
    };

    internal static bool OneDigitApart(string a, string b) =>
        a.Length == b.Length && a.Zip(b).Count(p => p.First != p.Second) == 1;

    private static bool SameName(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) == Normalize(b);

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
