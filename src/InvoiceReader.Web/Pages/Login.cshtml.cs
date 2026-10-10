using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using InvoiceReader.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InvoiceReader.Web.Pages;

public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    public LoginModel(IConfiguration config) => _config = config;

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }
    public string DemoEmail => _config["Demo:Email"] ?? "";

    public void OnGet()
    {
        Email = DemoEmail;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var okEmail = string.Equals(Email.Trim(), DemoEmail, StringComparison.OrdinalIgnoreCase);
        var okPassword = FixedTimeEquals(Password, _config["Demo:Password"] ?? "");
        if (!okEmail || !okPassword)
        {
            Error = "Pogrešan email ili lozinka.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, DemoEmail),
            new(UserExtensions.OfficeClaim, SeedData.DemoOfficeId.ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
