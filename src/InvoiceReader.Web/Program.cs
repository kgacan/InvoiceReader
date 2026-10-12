using System.Security.Claims;
using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Storage: SQLite file + uploads folder under DataDir ----
var dataDir = builder.Configuration["DataDir"] ?? "App_Data";
if (!Path.IsPathRooted(dataDir)) dataDir = Path.Combine(builder.Environment.ContentRootPath, dataDir);
Directory.CreateDirectory(dataDir);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataDir, "invoices.db")}"));
builder.Services.AddSingleton<FileStore>();

// ---- AI: Demo (no key), Claude or Gemini, chosen in configuration ----
var ai = builder.Configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
builder.Services.AddSingleton(ai);
builder.Services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddSingleton<IInvoiceReader>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ai");
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AI");
    switch (ai.Provider.Trim().ToLowerInvariant())
    {
        case "claude" when !string.IsNullOrWhiteSpace(ai.ClaudeApiKey):
            return new ClaudeInvoiceReader(http, ai);
        case "gemini" when !string.IsNullOrWhiteSpace(ai.GeminiApiKey):
            return new GeminiInvoiceReader(http, ai);
        case "claude":
        case "gemini":
            log.LogWarning("Ai:Provider is {Provider} but no API key is set; using Demo mode.", ai.Provider);
            break;
    }
    return new DemoInvoiceReader(sp.GetRequiredService<IWebHostEnvironment>());
});
builder.Services.AddHostedService<ProcessingWorker>();

// ---- Login (one demo user from configuration) ----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Upload");
    o.Conventions.AllowAnonymousToPage("/Error");
});

// Several phone photos in one upload.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 60 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 60 * 1024 * 1024);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Demo: create the schema directly. For production, switch to EF Core migrations.
    await db.Database.EnsureCreatedAsync();
    await SeedData.EnsureSeededAsync(db);
    await DemoInvoices.SeedAsync(db, scope.ServiceProvider.GetRequiredService<FileStore>(), app.Environment.ContentRootPath);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// The invoice image/PDF for the review screen, only for the logged-in office.
app.MapGet("/files/{id:int}", async (int id, ClaimsPrincipal user, AppDbContext db) =>
{
    var officeId = user.OfficeId();
    var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OfficeId == officeId);
    if (doc is null || !File.Exists(doc.StoredPath)) return Results.NotFound();
    return Results.File(doc.StoredPath, doc.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

public static class UserExtensions
{
    public const string OfficeClaim = "office_id";

    public static int OfficeId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(OfficeClaim), out var id) ? id : 0;
}
