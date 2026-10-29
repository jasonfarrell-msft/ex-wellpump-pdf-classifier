using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace WellpumpClassifier;

public static class Capability
{
    public const string CookieName = "wellpump-session";

    public static string Hash(string capability) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capability)));

    public static string? Owner(HttpContext context)
    {
        var value = context.Request.Cookies[CookieName];
        if (value is null || value.Length != 43) return null;
        try { return WebEncoders.Base64UrlDecode(value).Length == 32 ? Hash(value) : null; }
        catch (FormatException) { return null; }
    }

    public static string Grant(HttpContext context, bool development)
    {
        var owner = Owner(context);
        if (owner is not null) return owner;
        var capability = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(CookieName, capability, new CookieOptions
        {
            HttpOnly = true, Secure = !development, SameSite = SameSiteMode.Strict,
            Path = "/", MaxAge = TimeSpan.FromDays(7), IsEssential = true
        });
        return Hash(capability);
    }

    public static bool SameOrigin(string? origin, string expected) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.GetLeftPart(UriPartial.Authority).Equals(expected, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static bool Owns(Models.Job? job, string? owner, string id, DateTimeOffset now) =>
        owner is not null && job is not null && job.OwnerHash == owner && job.Id == id && job.ExpiresAt > now;
}
