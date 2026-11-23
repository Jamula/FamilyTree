using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class OidcConfigurationGuardAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var microsoftTenant = Guid.NewGuid();
        var google = new TrustedOidcIssuer("https://accounts.google.com", "google-client-id");
        var microsoft = new TrustedOidcIssuer(
            $"https://login.microsoftonline.com/{microsoftTenant:D}/v2.0",
            "microsoft-client-id");
        var policy = new OidcConfigurationGuard(new[] { google, microsoft });
        Check(policy.IsTrustedVerifiedIssuerAndAudience(google.Issuer, google.Audience),
            "exact Google issuer/audience accepted after signature verification");
        Check(policy.IsTrustedVerifiedIssuerAndAudience(microsoft.Issuer, microsoft.Audience),
            "exact tenant Microsoft issuer accepted");
        Check(!policy.IsTrustedVerifiedIssuerAndAudience(
            "https://accounts.google.com.attacker.invalid", google.Audience),
            "confusable issuer rejected");
        Check(!policy.IsTrustedVerifiedIssuerAndAudience(
            google.Issuer, "another-client"), "wrong audience rejected");
        Check(!policy.IsTrustedVerifiedIssuerAndAudience(
            microsoft.Issuer.ToUpperInvariant(), microsoft.Audience),
            "canonical verified issuer must match exactly");

        Reject(() => new OidcConfigurationGuard(Array.Empty<TrustedOidcIssuer>()),
            "empty provider list");
        Reject(() => new OidcConfigurationGuard(new[] {
            new TrustedOidcIssuer("http://accounts.google.com", "aud")
        }), "insecure issuer");
        Reject(() => new OidcConfigurationGuard(new[] {
            new TrustedOidcIssuer("https://evil.invalid", "aud")
        }), "unknown discovery host");
        Reject(() => new OidcConfigurationGuard(new[] {
            new TrustedOidcIssuer("https://login.microsoftonline.com/common/v2.0", "aud")
        }), "wildcard Microsoft tenant");
        Reject(() => new OidcConfigurationGuard(new[] {
            new TrustedOidcIssuer("https://accounts.google.com?redirect=https://evil.invalid", "aud")
        }), "query string on issuer");
        Reject(() => new OidcConfigurationGuard(new[] {
            new TrustedOidcIssuer("https://accounts.google.com", " ")
        }), "missing audience");
        Console.WriteLine("PASS trusted OIDC provider configuration negative tests");
    }
    private static void Check(bool ok, string why)
    { if (!ok) throw new InvalidOperationException(why); }
    private static void Reject(Action action, string why)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(why);
    }
}
