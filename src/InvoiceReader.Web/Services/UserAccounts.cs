using System.Security.Claims;
using System.Security.Cryptography;
using InvoiceReader.Web.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace InvoiceReader.Web.Services;

/// <summary>
/// Passwords and login claims. Passwords are stored as salted PBKDF2 hashes (ASP.NET Core's
/// PasswordHasher), never in plain text.
/// </summary>
public static class UserAccounts
{
    public const int MinPasswordLength = 10;

    public const string UserIdClaim = ClaimTypes.NameIdentifier;
    public const string StampClaim = "security_stamp";
    public const string AdminClaim = "is_admin";
    public const string AdminPolicy = "Admin";

    // Generated passwords: no 0/O, 1/l/I, so they can be read out over the phone.
    private const string PasswordChars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly PasswordHasher<User> Hasher = new();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static void SetPassword(User user, string password)
    {
        user.PasswordHash = Hasher.HashPassword(user, password);
        user.SecurityStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>True when the password matches. Upgrades the hash when the hasher asks for it.</summary>
    public static bool CheckPassword(User user, string password)
    {
        if (string.IsNullOrEmpty(user.PasswordHash)) return false;
        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = Hasher.HashPassword(user, password);
        return result != PasswordVerificationResult.Failed;
    }

    public static string GeneratePassword() => RandomNumberGenerator.GetString(PasswordChars, 14);

    /// <summary>Null when the password is acceptable, otherwise the message for the user.</summary>
    public static string? CheckNewPassword(string? password) =>
        (password ?? "").Length < MinPasswordLength
            ? $"Lozinka mora imati najmanje {MinPasswordLength} znakova."
            : null;

    public static ClaimsPrincipal ToPrincipal(User user)
    {
        var claims = new List<Claim>
        {
            new(UserIdClaim, user.Id.ToString()),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.Name) ? user.Email : user.Name),
            new(UserExtensions.OfficeClaim, user.OfficeId.ToString()),
            new(StampClaim, user.SecurityStamp)
        };
        if (user.IsAdmin) claims.Add(new Claim(AdminClaim, "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
