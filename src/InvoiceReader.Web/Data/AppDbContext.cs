using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Office> Offices => Set<Office>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<InvoiceData> Invoices => Set<InvoiceData>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<SupplierRule> SupplierRules => Set<SupplierRule>();
    public DbSet<ExportRecord> Exports => Set<ExportRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();

        b.Entity<Client>().HasIndex(c => c.UploadToken).IsUnique();

        b.Entity<Document>()
            .HasOne(d => d.Invoice)
            .WithOne(i => i.Document!)
            .HasForeignKey<InvoiceData>(i => i.DocumentId);

        b.Entity<Document>().HasIndex(d => new { d.OfficeId, d.Status });

        b.Entity<SupplierRule>()
            .HasIndex(r => new { r.OfficeId, r.ClientId, r.SupplierTaxId })
            .IsUnique();

        b.Entity<Account>().HasIndex(a => new { a.OfficeId, a.Code }).IsUnique();
    }
}
