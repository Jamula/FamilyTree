using System.Security.Claims;
using System.Security.Cryptography;
using FamilyTree.Api;
using FamilyTree.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

internal static class VerifiedFamilyMembershipAssertions
{
    internal static async Task RunAsync()
    {
        var directory = Path.Join(Path.GetTempPath(), "familytree-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var identityKey = RandomNumberGenerator.GetBytes(32);
        var google = new InteractiveOidc.Provider("google", "GoogleOidc",
            "https://accounts.google.com", "synthetic-client", "synthetic-secret", "/auth/callback/google");
        var microsoft = new InteractiveOidc.Provider("microsoft", "MicrosoftOidc",
            "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
            "synthetic-client", "synthetic-secret", "/auth/callback/microsoft");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(InteractiveOidc.ConfigureCookieOptions);
        using var root = services.BuildServiceProvider();
        var scopes = new List<IServiceScope>();

        DefaultHttpContext Context(string? cookie = null)
        {
            var scope = root.CreateScope();
            scopes.Add(scope);
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost");
            context.Response.Body = new MemoryStream();
            if (cookie is not null) context.Request.Headers.Cookie = cookie;
            return context;
        }

        async Task<string> Cookie(InteractiveOidc.Provider provider, string subject, bool expired = false)
        {
            var context = Context();
            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            VerifiedOidcSession.Stamp(identity, provider, provider.Authority, subject);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties
                {
                    IssuedUtc = DateTimeOffset.UtcNow.AddHours(-2),
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1)
                });
            return string.Join("; ", context.Response.Headers.SetCookie.Select(x => x!.Split(';')[0]));
        }

        try
        {
            var adminCookie = await Cookie(google, "synthetic-admin");
            var memberCookie = await Cookie(google, "synthetic-member");
            var sameSubjectOtherIssuer = await Cookie(microsoft, "synthetic-member");
            var unregisteredCookie = await Cookie(google, "synthetic-unregistered");
            var expiredCookie = await Cookie(google, "synthetic-member", expired: true);
            var alteredCookie = memberCookie[..(memberCookie.Length / 2)] + "INVALID" +
                memberCookie[(memberCookie.Length / 2)..];
            var family = Guid.NewGuid();
            var otherFamily = Guid.NewGuid();
            Guid admin;
            Guid member;
            Guid staleInvite;

            using (var registry = new SqlitePrincipalRegistry(path, identityKey))
            using (var store = new SqliteFamilyStore(path, key))
            {
                var bridge = new VerifiedFamilyMembership(registry, store, [google, microsoft]);
                admin = (await bridge.RegisterAsync(Context(adminCookie)))!.Value;
                member = (await bridge.RegisterAsync(Context(memberCookie)))!.Value;
                var otherIdentity = (await bridge.RegisterAsync(Context(sameSubjectOtherIssuer)))!.Value;
                Check(member != otherIdentity && member != admin, "issuer and subject bind distinct principals");
                Check(await bridge.RegisterAsync(Context(memberCookie)) == member, "registration is stable");
                Check(await bridge.RegisterAsync(Context(expiredCookie)) is null, "expired cookie denied");
                Check(await bridge.RegisterAsync(Context(alteredCookie)) is null, "tampered cookie denied");
                Check(!await bridge.IsActiveMemberAsync(Context(unregisteredCookie), family),
                    "unregistered verified session has no membership");
                Check(!await new VerifiedFamilyMembership(registry, store, [microsoft])
                    .IsActiveMemberAsync(Context(memberCookie), family),
                    "removing a provider invalidates its session");
                var spoofed = Context();
                spoofed.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("familytree:trusted:validated", "true"),
                    new Claim("familytree:trusted:provider", "google"),
                    new Claim("familytree:trusted:issuer", google.Authority),
                    new Claim("familytree:trusted:subject", "synthetic-member")
                ], "forged"));
                Check(await bridge.RegisterAsync(spoofed) is null, "injected principal denied");
                await ExpectDenied(() => bridge.AcceptInvitationAsync(spoofed, family, Guid.NewGuid()),
                    "injected principal cannot accept invitations");
                Check(!await bridge.IsActiveMemberAsync(Context(memberCookie), family),
                    "registered identity alone does not grant membership");
                store.CreateFictionalFamily(family, admin);
                store.CreateFictionalFamily(otherFamily, Guid.NewGuid());
                Check(!await bridge.IsActiveMemberAsync(Context(sameSubjectOtherIssuer), family),
                    "same subject at another issuer is not a member");
                var invite = await bridge.IssueInvitationAsync(Context(adminCookie), family, member,
                    DateTimeOffset.UtcNow.AddHours(1));
                var expiredInvite = store.IssueInvitation(family, admin, member,
                    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-2));
                await ExpectDenied(() => bridge.AcceptInvitationAsync(Context(memberCookie), family, expiredInvite),
                    "expired invitation denied");
                staleInvite = await bridge.IssueInvitationAsync(Context(adminCookie), family, member,
                    DateTimeOffset.UtcNow.AddHours(1));
                await ExpectDenied(() => bridge.AcceptInvitationAsync(Context(sameSubjectOtherIssuer), family, invite),
                    "wrong issuer cannot accept");
                await ExpectDenied(() => bridge.AcceptInvitationAsync(Context(memberCookie), otherFamily, invite),
                    "cross-family acceptance denied");
                await bridge.AcceptInvitationAsync(Context(memberCookie), family, invite);
                await ExpectDenied(() => bridge.AcceptInvitationAsync(Context(memberCookie), family, invite),
                    "invitation replay denied");
                Check(!await bridge.IsActiveMemberAsync(Context(memberCookie), family),
                    "pending membership denied");
                await ExpectDenied(() => bridge.ApproveMemberAsync(Context(memberCookie), family, member),
                    "self-approval denied");
                await ExpectDenied(() => bridge.ApproveMemberAsync(Context(adminCookie), otherFamily, member),
                    "cross-family approval denied");
                await bridge.ApproveMemberAsync(Context(adminCookie), family, member);
                Check(await bridge.IsActiveMemberAsync(Context(memberCookie), family),
                    "independently approved member active");
                Check(!await bridge.IsActiveMemberAsync(Context(memberCookie), otherFamily),
                    "cross-family membership denied");
            }

            using (var registry = new SqlitePrincipalRegistry(path, identityKey))
            using (var store = new SqliteFamilyStore(path, key))
            {
                var bridge = new VerifiedFamilyMembership(registry, store, [google, microsoft]);
                Check(await bridge.IsActiveMemberAsync(Context(memberCookie), family),
                    "identity and membership survive reopening");
                await bridge.RevokeMemberAsync(Context(adminCookie), family, member);
                Check(!await bridge.IsActiveMemberAsync(Context(memberCookie), family),
                    "existing cookie immediately denied after revocation");
                await ExpectDenied(() => bridge.AcceptInvitationAsync(Context(memberCookie), family, staleInvite),
                    "pre-revocation invitation denied");
                var rejoin = await bridge.IssueInvitationAsync(Context(adminCookie), family, member,
                    DateTimeOffset.UtcNow.AddHours(1));
                await bridge.AcceptInvitationAsync(Context(memberCookie), family, rejoin);
                Check(!await bridge.IsActiveMemberAsync(Context(memberCookie), family),
                    "rejoin requires independent approval");
                await bridge.ApproveMemberAsync(Context(adminCookie), family, member);
                Check(await bridge.IsActiveMemberAsync(Context(memberCookie), family),
                    "fresh invitation and approval permit rejoin");
                await bridge.RevokeMemberAsync(Context(adminCookie), family, member);
            }
            using (var registry = new SqlitePrincipalRegistry(path, identityKey))
            using (var store = new SqliteFamilyStore(path, key))
                Check(!await new VerifiedFamilyMembership(registry, store, [google, microsoft])
                    .IsActiveMemberAsync(Context(memberCookie), family), "revocation survives reopening");
            Console.WriteLine("PASS verified-cookie durable membership bridge");
        }
        finally
        {
            foreach (var scope in scopes) scope.Dispose();
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(identityKey);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task ExpectDenied(Func<Task> action, string message)
    {
        try { await action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }
}
