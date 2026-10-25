using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public LoginModel(AppDbContext db, IConfiguration config, AppMode mode)
    {
        _db = db;
        _config = config;
        Mode = mode;
    }

    public AppMode Mode { get; }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }
    public string DemoEmail => _config["Demo:Email"] ?? "";

    public void OnGet()
    {
        if (Mode.IsDemo) Email = DemoEmail;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var email = UserAccounts.NormalizeEmail(Email);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsActive);
        if (user is null || !UserAccounts.CheckPassword(user, Password))
        {
            Error = "Pogrešan email ili lozinka.";
            return Page();
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserAccounts.ToPrincipal(user),
            new AuthenticationProperties { IsPersistent = true });

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
