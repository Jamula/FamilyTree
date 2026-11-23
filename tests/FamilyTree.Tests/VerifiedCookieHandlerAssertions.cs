using System.Security.Claims;
using FamilyTree.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

internal static class VerifiedCookieHandlerAssertions
{
    internal static async Task RunAsync()
    {
        var provider = new InteractiveOidc.Provider("google", "GoogleOidc",
            "https://accounts.google.com", "synthetic-client", "synthetic-secret",
            "/auth/callback/google");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(InteractiveOidc.ConfigureCookieOptions);
        using var serviceProvider = services.BuildServiceProvider();

        DefaultHttpContext Context(string? cookie = null)
        {
            var c = new DefaultHttpContext { RequestServices = serviceProvider };
            c.Request.Scheme = "https";
            c.Request.Host = new HostString("localhost");
            c.Response.Body = new MemoryStream();
            if (!string.IsNullOrEmpty(cookie))
                c.Request.Headers.Cookie = cookie;
            return c;
        }

        async Task<string> SignInAsync(bool expired)
        {
            var context = Context();
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "fictional-oidc-sub")
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            VerifiedOidcSession.Stamp(identity, provider,
                provider.Authority, "fictional-oidc-sub");
            var properties = new AuthenticationProperties
            {
                IssuedUtc = expired
                    ? DateTimeOffset.UtcNow.AddHours(-2)
                    : DateTimeOffset.UtcNow,
                ExpiresUtc = expired
                    ? DateTimeOffset.UtcNow.AddHours(-1)
                    : DateTimeOffset.UtcNow.AddHours(1)
            };
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), properties);
            var headers = context.Response.Headers.SetCookie.ToArray();
            var pieces = headers.Select(value => value!.Split(';')[0]).ToArray();
            Check(pieces.Length > 0 &&
                pieces[0].StartsWith("__Host-FamilyTreeSession=", StringComparison.Ordinal),
                "protected __Host cookie set");
            Check(headers.All(value => value is not null &&
                    value.Contains("secure", StringComparison.OrdinalIgnoreCase)),
                "all cookie chunks must carry Secure attribute");
            Check(headers.All(value => value is not null &&
                    value.Contains("httponly", StringComparison.OrdinalIgnoreCase)),
                "all cookie chunks must carry HttpOnly attribute");
            // CookieAuthentication's chunking manager may emit several
            // Set-Cookie headers. Replay ALL protected cookie fragments.
            return string.Join("; ", pieces);
        }

        var goodCookie = await SignInAsync(expired: false);
        var protectedContext = Context(goodCookie);
        var ticket = await protectedContext.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        Console.WriteLine("COOKIE synthetic probe: succeeded=" + ticket.Succeeded +
            " principalAuthenticated=" +
            (ticket.Principal?.Identity?.IsAuthenticated ?? false) +
            " trustedClaimTypes=" +
            (ticket.Principal?.Claims.Count(c =>
                c.Type.StartsWith("familytree:trusted:", StringComparison.OrdinalIgnoreCase)) ?? 0) +
            " failed=" + (ticket.Failure?.GetType().Name ?? "none"));
        Check(await VerifiedOidcSession.ReadAsync(protectedContext, [provider]),
            "real ASP.NET cookie handler accepts cryptographically protected ticket");
        Check(!await VerifiedOidcSession.ReadAsync(Context(goodCookie), []),
            "deconfigured provider invalidates even a still-valid cookie");
        var modified = goodCookie[..(goodCookie.Length / 2)] + "INVALID" +
            goodCookie[(goodCookie.Length / 2)..];
        Check(!await VerifiedOidcSession.ReadAsync(Context(modified), [provider]),
            "modified encrypted ticket fails authentication");
        var expiredCookie = await SignInAsync(expired: true);
        Check(!await VerifiedOidcSession.ReadAsync(Context(expiredCookie), [provider]),
            "expired signed ticket denied");
        var spoofed = Context();
        spoofed.Request.Headers.Authorization = "Bearer synthetically-forged";
        spoofed.Request.Headers["X-Family-Admin"] = "true";
        var identityInjected = new ClaimsIdentity([
            new Claim("familytree:trusted:validated", "true"),
            new Claim("familytree:trusted:provider", "google"),
            new Claim("familytree:trusted:issuer", provider.Authority),
            new Claim("familytree:trusted:subject", "fake-principal")
        ], "Synthetic");
        spoofed.User = new ClaimsPrincipal(identityInjected);
        Check(!await VerifiedOidcSession.ReadAsync(spoofed, [provider]),
            "request headers or injected User cannot bypass a missing cookie");
        Console.WriteLine("PASS real cookie handler: verified, expired, tampered, removed-provider, and forged identity");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
