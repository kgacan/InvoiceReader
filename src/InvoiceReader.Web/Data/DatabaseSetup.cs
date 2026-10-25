using InvoiceReader.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Data;

/// <summary>Brings the database schema up to date on start, and creates the app owner's login.</summary>
public static class DatabaseSetup
{
    public const string AdminOfficeName = "Moj ured (vlasnik aplikacije)";

    /// <summary>
    /// Applies EF Core migrations. Databases made by the earlier demo (EnsureCreated) have tables
    /// but no migration history: a demo one is recreated, anything else stops the app so no real
    /// data is touched.
    /// </summary>
    public static async Task MigrateAsync(AppDbContext db, bool isDemo, ILogger log)
    {
        if (await IsPreMigrationsDatabaseAsync(db))
        {
            if (!isDemo)
                throw new InvalidOperationException(
                    "The database was created without EF Core migrations. Back it up, then delete it or point DataDir to an empty folder.");
            log.LogWarning("Demo database from before migrations: recreating it.");
            await db.Database.EnsureDeletedAsync();
        }
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Creates the owner's login from Admin:Email / Admin:Password the first time, in an office of
    /// its own (handy for trying real invoices). Later starts leave the user alone, so a password
    /// changed in the app is kept.
    /// </summary>
    public static async Task EnsureAdminAsync(AppDbContext db, string? email, string? password, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        email = UserAccounts.NormalizeEmail(email);
        if (await db.Users.AnyAsync(u => u.Email == email)) return;

        if (UserAccounts.CheckNewPassword(password) is not null)
        {
            log.LogWarning("Admin:Password is missing or shorter than {Min} characters; admin user not created.",
                UserAccounts.MinPasswordLength);
            return;
        }

        var office = new Office { Name = AdminOfficeName };
        var user = new User { Office = office, Email = email, Name = email, IsAdmin = true };
        UserAccounts.SetPassword(user, password!);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        log.LogInformation("Created admin user {Email}.", email);
    }

    private static async Task<bool> IsPreMigrationsDatabaseAsync(AppDbContext db)
    {
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        return tables.Contains("Offices") && !tables.Contains("__EFMigrationsHistory");
    }
}
