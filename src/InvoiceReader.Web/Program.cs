using System.Security.Claims;
using System.Threading.RateLimiting;
using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var mode = AppMode.From(builder.Configuration);
builder.Services.AddSingleton(mode);

// ---- Storage: SQLite file, uploads and login keys under DataDir (a mounted disk in production) ----
var dataDir = builder.Configuration["DataDir"] ?? "App_Data";
if (!Path.IsPathRooted(dataDir)) dataDir = Path.Combine(builder.Environment.ContentRootPath, dataDir);
Directory.CreateDirectory(dataDir);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataDir, "invoices.db")}"));
builder.Services.AddSingleton<FileStore>();

// Keys that encrypt the login cookie. Kept on the data disk so a redeploy doesn't log everyone out.
builder.Services.AddDataProtection()
    .SetApplicationName("InvoiceReader")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

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

// ---- Login: users in the database, one office each ----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        // Logged in but not allowed (an accountant opening /Admin): plain 403, no access-denied page.
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        // A deactivated user, or a session from before a password change, is logged out.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var stamp = ctx.Principal?.FindFirstValue(UserAccounts.StampClaim);
            var valid = int.TryParse(ctx.Principal?.FindFirstValue(UserAccounts.UserIdClaim), out var userId)
                && await db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.SecurityStamp == stamp);
            if (!valid)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization(o =>
    o.AddPolicy(UserAccounts.AdminPolicy, p => p.RequireClaim(UserAccounts.AdminClaim, "true")));

// Slows down password guessing: 20 login requests per 5 minutes per IP address.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(5) }));
});

// Behind the host's proxy (Render, Caddy): take the client IP and https from the header the
// proxy adds. Only the last hop is used (ForwardLimit 1), so a client can't fake its own.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AuthorizePage("/Admin", UserAccounts.AdminPolicy);
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
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var config = app.Configuration;

    await DatabaseSetup.MigrateAsync(db, mode.IsDemo, log);
    if (mode.IsDemo)
    {
        await SeedData.EnsureDemoOfficeAsync(db);
        await SeedData.EnsureDemoUserAsync(db, config["Demo:Email"] ?? "", config["Demo:Password"] ?? "");
        await DemoInvoices.SeedAsync(db, scope.ServiceProvider.GetRequiredService<FileStore>(), app.Environment.ContentRootPath);
    }
    await DatabaseSetup.EnsureAdminAsync(db, config["Admin:Email"], config["Admin:Password"], log);

    if (!mode.IsDemo)
    {
        // Real invoices must be read by a real AI, never silently left empty.
        if (scope.ServiceProvider.GetRequiredService<IInvoiceReader>() is DemoInvoiceReader)
            throw new InvalidOperationException("Mode=Pilot needs a real AI reader: set Ai:Provider and its API key.");
        if (!await db.Users.AnyAsync())
            log.LogWarning("No users yet: set Admin:Email and Admin:Password, then restart.");
    }
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// The invoice image/PDF for the review screen, only for the logged-in office.
app.MapGet("/files/{id:int}", async (int id, ClaimsPrincipal user, AppDbContext db, FileStore files) =>
{
    var officeId = user.OfficeId();
    var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OfficeId == officeId);
    if (doc is null) return Results.NotFound();
    var path = files.FullPath(doc);
    if (!File.Exists(path)) return Results.NotFound();
    return Results.File(path, doc.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

public static class UserExtensions
{
    public const string OfficeClaim = "office_id";

    public static int OfficeId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(OfficeClaim), out var id) ? id : 0;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.HasClaim(UserAccounts.AdminClaim, "true");
}
