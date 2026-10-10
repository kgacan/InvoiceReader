using System.ComponentModel.DataAnnotations.Schema;

namespace InvoiceReader.Web.Data;

/// <summary>An accounting office: the paying customer of the product.</summary>
public class Office
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string AccountingProgram { get; set; } = "Generic CSV";

    /// <summary>Account for input VAT (example value, each office sets its own).</summary>
    public string VatInputAccount { get; set; } = "";

    /// <summary>Account for domestic suppliers (example value, each office sets its own).</summary>
    public string SuppliersAccount { get; set; } = "";
}

/// <summary>A firm whose books the office keeps.</summary>
public class Client
{
    public int Id { get; set; }
    public int OfficeId { get; set; }
    public string Name { get; set; } = "";
    public string TaxId { get; set; } = "";

    /// <summary>Secret token in the client's upload link (/u/{token}).</summary>
    public string UploadToken { get; set; } = "";

    public List<Document> Documents { get; set; } = new();
}

public enum DocumentStatus
{
    Uploaded = 0,
    Reading = 1,
    NeedsReview = 2,
    Confirmed = 3,
    Exported = 4,
    Failed = 5
}

/// <summary>One uploaded file (photo or PDF of an invoice).</summary>
public class Document
{
    public int Id { get; set; }
    public int OfficeId { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }

    public string OriginalFileName { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;
    public string? Error { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public InvoiceData? Invoice { get; set; }
}

/// <summary>What the AI read from the document, and what the accountant corrected.</summary>
public class InvoiceData
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public Document? Document { get; set; }

    public string? SupplierName { get; set; }
    public string? SupplierTaxId { get; set; }
    public string? SupplierVatNumber { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public decimal? NetAmount { get; set; }
    public decimal? VatAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string Currency { get; set; } = "BAM";

    /// <summary>Cost account suggested by a supplier rule or picked by the accountant.</summary>
    public string? AccountCode { get; set; }

    /// <summary>True when AccountCode came from a learned supplier rule.</summary>
    public bool AccountSuggested { get; set; }

    public double? Confidence { get; set; }
    public string ModelUsed { get; set; } = "";

    /// <summary>Warnings from the checks, one per line.</summary>
    public string Warnings { get; set; } = "";

    public string RawJson { get; set; } = "";
    public DateTime? ConfirmedAt { get; set; }

    [NotMapped]
    public IEnumerable<string> WarningList =>
        Warnings.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>One line of the office's chart of accounts (kontni plan).</summary>
public class Account
{
    public int Id { get; set; }
    public int OfficeId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Learned rule: invoices from this supplier go to this account.</summary>
public class SupplierRule
{
    public int Id { get; set; }
    public int OfficeId { get; set; }
    public int ClientId { get; set; }
    public string SupplierTaxId { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public int TimesUsed { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A record of each export file produced.</summary>
public class ExportRecord
{
    public int Id { get; set; }
    public int OfficeId { get; set; }
    public int ClientId { get; set; }
    public string Period { get; set; } = "";
    public string FileName { get; set; } = "";
    public int InvoiceCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
