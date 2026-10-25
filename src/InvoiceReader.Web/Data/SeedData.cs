using System.Security.Cryptography;
using InvoiceReader.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Data;

/// <summary>
/// Demo mode only: one demo office, three fictional clients, an EXAMPLE chart of accounts and
/// the demo login. All names and numbers are invented for the demo.
/// </summary>
public static class SeedData
{
    public const int DemoOfficeId = 1;

    /// <summary>The demo login always matches Demo:Email / Demo:Password from configuration.</summary>
    public static async Task EnsureDemoUserAsync(AppDbContext db, string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return;
        email = UserAccounts.NormalizeEmail(email);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new User { OfficeId = DemoOfficeId, Email = email, Name = email };
            db.Users.Add(user);
        }
        if (!UserAccounts.CheckPassword(user, password)) UserAccounts.SetPassword(user, password);
        user.IsActive = true;
        await db.SaveChangesAsync();
    }

    public static async Task EnsureDemoOfficeAsync(AppDbContext db)
    {
        if (await db.Offices.AnyAsync(o => o.Id == DemoOfficeId)) return;

        var office = new Office
        {
            Id = DemoOfficeId,
            Name = "Demo knjigovodstveni ured",
            AccountingProgram = "Generic CSV",
            // Example values only: every office replaces these with its own accounts.
            VatInputAccount = "2700",
            SuppliersAccount = "4320"
        };
        db.Offices.Add(office);

        db.Clients.AddRange(
            new Client { OfficeId = DemoOfficeId, Name = "Primjer Market d.o.o.", TaxId = "4200000000101", UploadToken = NewToken() },
            new Client { OfficeId = DemoOfficeId, Name = "Demo Pekara d.o.o.", TaxId = "4200000000202", UploadToken = NewToken() },
            new Client { OfficeId = DemoOfficeId, Name = "Test Servis s.p.", TaxId = "4200000000303", UploadToken = NewToken() });

        // EXAMPLE chart of accounts for the demo. Not an official list:
        // a real office uploads its own kontni plan on the Settings page.
        var accounts = new (string Code, string Name)[]
        {
            ("5120", "Troškovi električne energije"),
            ("5121", "Troškovi goriva"),
            ("5100", "Troškovi kancelarijskog materijala"),
            ("5310", "Troškovi telefona i interneta"),
            ("5330", "Troškovi usluga održavanja"),
            ("5350", "Troškovi reklame i propagande"),
            ("5500", "Troškovi neproizvodnih usluga"),
            ("5530", "Troškovi platnog prometa"),
            ("5540", "Troškovi zakupa"),
            ("2700", "Ulazni PDV"),
            ("4320", "Dobavljači u zemlji")
        };
        foreach (var (code, name) in accounts)
            db.Accounts.Add(new Account { OfficeId = DemoOfficeId, Code = code, Name = name });

        await db.SaveChangesAsync();
    }

    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
