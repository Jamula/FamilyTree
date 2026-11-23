using System.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace FamilyTree.Api;

/// <summary>
/// Verified OIDC subject binding for signed ASP.NET Core session cookies.
/// Only call Stamp from the OIDC middleware's OnTokenValidated event AFTER
/// the framework verifies signature, issuer, audience, lifetime and nonce.
/// ReadAsync independently verifies the authenticated cookie ticket.
/// A verified session never implies family membership or permission.
/// </summary>
public static class VerifiedOidcSession
{
    private const string Prefix = "familytree:trusted:";
    private const string Marker = Prefix + "validated";
    private const string ProviderClaim = Prefix + "provider";
    private const string IssuerClaim = Prefix + "issuer";
    private const string SubjectClaim = Prefix + "subject";

    internal static void Stamp(ClaimsIdentity identity, InteractiveOidc.Provider provider,
        string? verifiedIssuer, string? verifiedSubject)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(provider);
        if (!identity.IsAuthenticated ||
            string.IsNullOrWhiteSpace(verifiedIssuer) ||
            string.IsNullOrWhiteSpace(provider.Authority) ||
            !string.Equals(verifiedIssuer, provider.Authority, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(verifiedSubject) ||
            verifiedSubject.Length > 256 || verifiedSubject.Any(char.IsControl) ||
            provider.Name is not ("google" or "microsoft"))
            throw new SecurityException("Verified provider subject binding failed.");

        // Never preserve custom claims provided by the identity provider:
        // their names are not reserved on the external OIDC token.
        foreach (var old in identity.Claims.Where(c => c.Type.StartsWith(Prefix,
            StringComparison.OrdinalIgnoreCase)).ToArray())
            identity.RemoveClaim(old);
        identity.AddClaim(new Claim(Marker, "true"));
        identity.AddClaim(new Claim(ProviderClaim, provider.Name));
        identity.AddClaim(new Claim(IssuerClaim, verifiedIssuer!));
        identity.AddClaim(new Claim(SubjectClaim, verifiedSubject));
    }

    internal static bool IsVerifiedOidcCookiePrincipal(ClaimsPrincipal? principal,
        IReadOnlyList<InteractiveOidc.Provider> configuredProviders)
    {
        if (principal?.Identity?.IsAuthenticated != true || configuredProviders is null)
            return false;
        static string? Unique(ClaimsPrincipal user, string type)
        {
            var claims = user.FindAll(type).Select(c => c.Value).ToArray();
            return claims.Length == 1 ? claims[0] : null;
        }
        if (Unique(principal, Marker) != "true")
            return false;
        var providerName = Unique(principal, ProviderClaim);
        var issuer = Unique(principal, IssuerClaim);
        var subject = Unique(principal, SubjectClaim);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 ||
            subject.Any(char.IsControl))
            return false;
        return configuredProviders.Any(provider =>
            string.Equals(provider.Name, providerName, StringComparison.Ordinal) &&
            string.Equals(provider.Authority, issuer, StringComparison.Ordinal));
    }

    public static async Task<bool> ReadAsync(HttpContext context,
        IReadOnlyList<InteractiveOidc.Provider> configuredProviders)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuredProviders);
        // Headers and caller-injected HttpContext.User do not satisfy this check:
        // only ASP.NET Core's authenticated, encrypted/signed cookie ticket can.
        var ticket = await context.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        return ticket.Succeeded &&
            IsVerifiedOidcCookiePrincipal(ticket.Principal, configuredProviders);
    }
}
