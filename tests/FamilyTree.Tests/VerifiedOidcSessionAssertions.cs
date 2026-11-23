using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using FamilyTree.Api;

internal static class VerifiedOidcSessionAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var provider = new InteractiveOidc.Provider("google","GoogleOidc",
            "https://accounts.google.com","synthetic-client","synthetic-secret",
            "/auth/callback/google");
        var configured = new[] { provider };
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "synthetic-oidc-subject"),
            new Claim("familytree:trusted:validated", "spoofed"),
            new Claim("familytree:trusted:issuer", "https://untrusted.example"),
        ], "OIDC");
        var principal = new ClaimsPrincipal(identity);
        Check(!VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, configured),
            "untrusted session claims are never accepted");
        VerifiedOidcSession.Stamp(identity, provider, provider.Authority,
            "synthetic-oidc-subject");
        Check(VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, configured),
            "verified, single-provider binding accepted");
        Check(identity.FindAll("familytree:trusted:issuer").Count() == 1,
            "identity-provider reserved claims replaced");
        Check(!VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, []),
            "removed provider configuration invalidates sessions");
        Check(!VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, [
            provider with {Authority="https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0"}
        ]), "provider authority mismatch invalidates session");
        identity.AddClaim(new Claim("familytree:trusted:validated","true"));
        Check(!VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, configured),
            "duplicate marker claims denied");
        identity.RemoveClaim(identity.FindAll("familytree:trusted:validated").Last());
        Check(VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, configured),
            "restored unique marker accepted");
        identity.RemoveClaim(identity.FindFirst("familytree:trusted:subject")!);
        Check(!VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, configured),
            "missing subject denies session");
        VerifiedOidcSession.Stamp(identity, provider, provider.Authority, "synthetic-new");
        Expect<SecurityException>(()=>VerifiedOidcSession.Stamp(identity, provider,
            "https://evil.example", "synthetic-new"), "issuer mismatch");
        Expect<SecurityException>(()=>VerifiedOidcSession.Stamp(identity, provider,
            provider.Authority, ""), "empty subject");
        Expect<SecurityException>(()=>VerifiedOidcSession.Stamp(identity, provider,
            provider.Authority, "Bad\nSubject"), "control character subject");
        Expect<SecurityException>(()=>VerifiedOidcSession.Stamp(identity, provider,
            provider.Authority, new string('A',257)), "oversized subject");
        Expect<SecurityException>(()=>VerifiedOidcSession.Stamp(
            new ClaimsIdentity(), provider, provider.Authority,"synthetic-new"),
            "unauthenticated identity denied");
        Expect<SecurityException>(()=>VerifiedOidcSession.Stamp(identity,
            provider with {Name="untrusted"},provider.Authority,"synthetic-new"),
            "untrusted provider denied");
        Expect<ArgumentNullException>(()=>VerifiedOidcSession.Stamp(
            null!,provider,provider.Authority,"synthetic-new"),"null identity rejected");
        Expect<ArgumentNullException>(()=>VerifiedOidcSession.Stamp(
            identity,null!,provider.Authority,"synthetic-new"),"null provider rejected");
        Check(!VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(null, configured),
            "null principal denied");
        Check(!VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, null!),
            "null provider configuration denied");
        Console.WriteLine("PASS OIDC validated-subject session binding, claim spoofing and negative paths");
    }
    private static void Check(bool ok,string message)
    {if(!ok)throw new InvalidOperationException(message);}
    private static void Expect<T>(Action action,string message) where T:Exception
    {try{action();return;}catch(T){return;}throw new InvalidOperationException(message);}
}
