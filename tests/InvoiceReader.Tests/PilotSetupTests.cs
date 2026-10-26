using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvoiceReader.Tests;

public sealed class PilotSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "invoicereader-tests", Guid.NewGuid().ToString("N"));

    public PilotSetupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);

    [Fact]
    public void Password_hash_verifies_and_new_password_changes_the_stamp()
    {
        var user = new User { Email = "a@b.ba" };
        UserAccounts.SetPassword(user, "prva-lozinka-123");
        var firstStamp = user.SecurityStamp;

        Assert.DoesNotContain("prva-lozinka-123", user.PasswordHash);
        Assert.True(UserAccounts.CheckPassword(user, "prva-lozinka-123"));
        Assert.False(UserAccounts.CheckPassword(user, "pogresna-lozinka"));

        UserAccounts.SetPassword(user, "druga-lozinka-456");
        Assert.NotEqual(firstStamp, user.SecurityStamp);
        Assert.False(UserAccounts.CheckPassword(user, "prva-lozinka-123"));
    }

    [Fact]
    public void Generated_passwords_are_long_enough_and_avoid_lookalike_characters()
    {
        var password = UserAccounts.GeneratePassword();
        Assert.Null(UserAccounts.CheckNewPassword(password));
        Assert.DoesNotContain(password, c => "0O1lI".Contains(c));
        Assert.NotEqual(password, UserAccounts.GeneratePassword());
        Assert.NotNull(UserAccounts.CheckNewPassword("kratka"));
    }

    [Fact]
    public void Mode_defaults_to_demo_and_rejects_unknown_values()
    {
        static AppMode From(string? value) => AppMode.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mode"] = value }).Build());

        Assert.True(From(null).IsDemo);
        Assert.True(From("demo").IsDemo);
        Assert.False(From("Pilot").IsDemo);
        Assert.Throws<InvalidOperationException>(() => From("Produkcija"));
    }

    [Fact]
    public async Task Files_are_stored_relative_to_the_uploads_folder()
    {
        var store = new FileStore(Path.Combine(_dir, "uploads"));
        var pdf = "%PDF-1.4 test"u8.ToArray();
        using var content = new MemoryStream(pdf);

        var (doc, error) = await store.SaveAsync(content, "Faktura.PDF", content.Length, officeId: 7, clientId: 1);

        Assert.Null(error);
        Assert.False(Path.IsPathRooted(doc!.StoredPath));
        Assert.StartsWith("7/", doc.StoredPath);
        Assert.EndsWith(".pdf", doc.StoredPath);
        Assert.Equal(pdf, await store.ReadAsync(doc, CancellationToken.None));

        // Rows from before relative paths keep working.
        var legacy = new Document { StoredPath = store.FullPath(doc) };
        Assert.Equal(store.FullPath(doc), store.FullPath(legacy));
    }

    [Fact]
    public async Task Migrations_create_the_schema_and_match_the_model()
    {
        await using var db = NewDb();
        await DatabaseSetup.MigrateAsync(db, isDemo: false, NullLogger.Instance);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(), "Model changed: run dotnet ef migrations add <Name>.");
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Database_from_before_migrations_is_recreated_in_demo_but_stops_a_pilot()
    {
        await using (var old = NewDb())
            await old.Database.EnsureCreatedAsync();

        await using (var pilot = NewDb())
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSetup.MigrateAsync(pilot, isDemo: false, NullLogger.Instance));

        await using var demo = NewDb();
        await DatabaseSetup.MigrateAsync(demo, isDemo: true, NullLogger.Instance);
        Assert.Contains("20261025185325_InitialCreate", await demo.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Admin_is_created_once_in_its_own_office_and_keeps_a_changed_password()
    {
        await using var db = NewDb();
        await DatabaseSetup.MigrateAsync(db, isDemo: false, NullLogger.Instance);

        await DatabaseSetup.EnsureAdminAsync(db, " Vlasnik@Primjer.ba ", "pocetna-lozinka-1", NullLogger.Instance);
        var admin = await db.Users.Include(u => u.Office).SingleAsync();
        Assert.True(admin.IsAdmin);
        Assert.Equal("vlasnik@primjer.ba", admin.Email);
        Assert.Equal(DatabaseSetup.AdminOfficeName, admin.Office!.Name);

        UserAccounts.SetPassword(admin, "promijenjena-lozinka");
        await db.SaveChangesAsync();
        await DatabaseSetup.EnsureAdminAsync(db, "vlasnik@primjer.ba", "pocetna-lozinka-1", NullLogger.Instance);

        Assert.Equal(1, await db.Users.CountAsync());
        Assert.True(UserAccounts.CheckPassword(await db.Users.SingleAsync(), "promijenjena-lozinka"));
    }

    [Fact]
    public async Task Admin_with_a_short_password_is_not_created()
    {
        await using var db = NewDb();
        await DatabaseSetup.MigrateAsync(db, isDemo: false, NullLogger.Instance);

        await DatabaseSetup.EnsureAdminAsync(db, "vlasnik@primjer.ba", "kratka", NullLogger.Instance);

        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.Offices.CountAsync());
    }

    [Fact]
    public async Task Demo_login_follows_the_configured_password()
    {
        await using var db = NewDb();
        await DatabaseSetup.MigrateAsync(db, isDemo: true, NullLogger.Instance);
        await SeedData.EnsureDemoOfficeAsync(db);

        await SeedData.EnsureDemoUserAsync(db, "demo@demo.ba", "demo1234");
        await SeedData.EnsureDemoUserAsync(db, "demo@demo.ba", "nova-demo-lozinka");

        var user = await db.Users.SingleAsync();
        Assert.Equal(SeedData.DemoOfficeId, user.OfficeId);
        Assert.False(user.IsAdmin);
        Assert.True(UserAccounts.CheckPassword(user, "nova-demo-lozinka"));
    }
}
