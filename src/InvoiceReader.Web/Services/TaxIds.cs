namespace InvoiceReader.Web.Services;

/// <summary>
/// BiH VAT number rule (UIO Pravilnik o registraciji i upisu u Jedinstveni registar obveznika
/// indirektnih poreza, Sl. glasnik BiH 51/12, cl. 19.-23.): the 12-digit PDV broj is the
/// 13-digit ID broj (JIB) without its leading "4". So for a VAT payer, JIB = "4" + PDV broj.
/// </summary>
public static class TaxIds
{
    /// <summary>The 13-digit ID broj that belongs to a 12-digit PDV broj, or null.</summary>
    public static string? IdFromVat(string? vat) =>
        vat is { Length: 12 } && vat.All(char.IsDigit) ? "4" + vat : null;

    public static bool IsWellFormedId(string? id) =>
        id is { Length: 13 } && id[0] == '4' && id.All(char.IsDigit);

    /// <summary>
    /// Fills in a missing or malformed ID broj from the PDV broj. A well-formed ID that disagrees
    /// with the PDV broj is left alone (either could be misread); <see cref="InvoiceChecks"/> warns.
    /// </summary>
    public static (AiInvoice Invoice, string? Note) Reconcile(AiInvoice inv)
    {
        var fromVat = IdFromVat(inv.SupplierVatNumber);
        if (fromVat is null || inv.SupplierTaxId == fromVat || IsWellFormedId(inv.SupplierTaxId))
            return (inv, null);

        var note = inv.SupplierTaxId is null
            ? $"ID broj dobavljača nije na računu; izveden iz PDV broja: {fromVat} (ID broj = 4 + PDV broj)."
            : $"ID broj dobavljača pročitan kao {inv.SupplierTaxId}, ispravljen na {fromVat} prema PDV broju (ID broj = 4 + PDV broj).";
        return (inv with { SupplierTaxId = fromVat }, note);
    }
}
