using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using FamilyTree.Api;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;

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
        identity.AddClaim(new Claim("FamilyTree:trusted:issuer", "external-spoof"));
        VerifiedOidcSession.Stamp(identity, provider, provider.Authority,
            "synthetic-oidc-subject");
        Check(VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(principal, configured),
            "verified, single-provider binding accepted");
        Check(identity.FindAll("familytree:trusted:issuer").Count() == 1,
            "identity-provider reserved claims replaced");
        Check(!identity.Claims.Any(x => x.Value == "external-spoof"),
            "mixed-case reserved claims cleared before cookie issuance");
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
        // Invoke the real OnTokenValidated callback under synthetic contexts,
        // never treating these unsigned test JWTs as externally verified tokens.
        var options = new OpenIdConnectOptions();
        InteractiveOidc.ConfigureProviderOptions(options, provider);
        CheckCallback(options, provider, new ClaimsIdentity([
            new Claim("sub", "fictional-raw")
        ], "OIDC"), provider.Authority, true, "raw sub");
        CheckCallback(options, provider, new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "fictional-mapped")
        ], "OIDC"), provider.Authority, true, "mapped sub");
        CheckCallback(options, provider, new ClaimsIdentity([
            new Claim("sub", "first"), new Claim("sub", "second")
        ], "OIDC"), provider.Authority, false, "duplicate raw subjects");
        CheckCallback(options, provider, new ClaimsIdentity(), provider.Authority,
            false, "anonymous identity");
        CheckCallback(options, provider, new ClaimsIdentity([
            new Claim("sub", "fictional")
        ], "OIDC"), "https://wrong.example", false, "issuer mismatch");
        CheckCallback(options, provider, new ClaimsIdentity([
            new Claim("sub", "Bad\nSubject")
        ], "OIDC"), provider.Authority, false, "control character subject");
        Console.WriteLine("PASS OIDC validated-subject session binding, claim spoofing and negative paths");
    }
    private static void CheckCallback(OpenIdConnectOptions options,
        InteractiveOidc.Provider provider, ClaimsIdentity identity,
        string issuer, bool success, string scenario)
    {
        var scheme = new AuthenticationScheme(provider.Scheme,
            provider.Scheme, typeof(OpenIdConnectHandler));
        var principal = new ClaimsPrincipal(identity);
        var context = new TokenValidatedContext(new DefaultHttpContext(),
            scheme, options, principal, new AuthenticationProperties())
        {
            SecurityToken = new JwtSecurityToken(issuer: issuer)
        };
        options.Events.OnTokenValidated(context).GetAwaiter().GetResult();
        Check((context.Result?.Failure is null) == success,
            "OIDC token callback: " + scenario);
        if (success)
            Check(VerifiedOidcSession.IsVerifiedOidcCookiePrincipal(
                principal, [provider]),
                "OIDC callback must stamp exact provider and subject: " + scenario);
    }
    private static void Check(bool ok,string message)
    {if(!ok)throw new InvalidOperationException(message);}
    private static void Expect<T>(Action action,string message) where T:Exception
    {var caught=false;try{action();}catch(T){caught=true;}if(!caught)throw new InvalidOperationException(message);}
}
