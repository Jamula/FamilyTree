using System.Security.Claims;
using System.Security.Cryptography;
using FamilyTree.Api;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

internal static class ProfileClaimPersistenceAssertions
{
    internal static async Task RunAsync()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-profile-claims-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var identityKey = RandomNumberGenerator.GetBytes(32);
        var provider = new InteractiveOidc.Provider("google", "GoogleOidc",
            "https://accounts.google.com", "synthetic-client", "synthetic-secret",
            "/auth/callback/google");
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

        async Task<string> Cookie(string subject)
        {
            var context = Context();
            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            VerifiedOidcSession.Stamp(identity, provider, provider.Authority, subject);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties
                {
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
                });
            return string.Join("; ",
                context.Response.Headers.SetCookie.Select(value => value!.Split(';')[0]));
        }

        try
        {
            var adminCookie = await Cookie("synthetic-profile-admin");
            var claimantCookie = await Cookie("synthetic-profile-claimant");
            var reviewerCookie = await Cookie("synthetic-profile-reviewer");
            var pendingCookie = await Cookie("synthetic-profile-pending");
            var noViewCookie = await Cookie("synthetic-profile-no-view");
            var otherAdminCookie = await Cookie("synthetic-profile-other-admin");
            var familyId = Guid.NewGuid();
            var otherFamilyId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var claimantId = Guid.Empty;
            var reviewerId = Guid.Empty;
            var adminId = Guid.Empty;
            var personId = Guid.NewGuid();
            var secondPersonId = Guid.NewGuid();
            SqliteFamilyStore.StoredProfileClaim? approvedClaim = null;
            SqliteFamilyStore.StoredProfileClaim? restartPendingClaim = null;

            using (var registry = new SqlitePrincipalRegistry(path, identityKey))
            using (var store = new SqliteFamilyStore(path, key))
            {
                var bridge = new VerifiedFamilyMembership(registry, store, [provider]);
                adminId = (await bridge.RegisterAsync(Context(adminCookie)))!.Value;
                claimantId = (await bridge.RegisterAsync(Context(claimantCookie)))!.Value;
                reviewerId = (await bridge.RegisterAsync(Context(reviewerCookie)))!.Value;
                var pendingId = (await bridge.RegisterAsync(Context(pendingCookie)))!.Value;
                var noViewId = (await bridge.RegisterAsync(Context(noViewCookie)))!.Value;
                var otherAdminId = (await bridge.RegisterAsync(Context(otherAdminCookie)))!.Value;
                store.CreateFictionalFamily(familyId, adminId);
                store.CreateFictionalFamily(otherFamilyId, otherAdminId);
                Activate(store, familyId, adminId, claimantId);
                Activate(store, familyId, adminId, reviewerId);
                Activate(store, familyId, adminId, noViewId);
                store.PromoteAdministrator(familyId, adminId, reviewerId);
                var pendingInvite = store.IssueInvitation(familyId, adminId, pendingId,
                    DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
                store.AcceptInvitation(familyId, pendingId, pendingInvite, DateTimeOffset.UtcNow);

                var firstPerson = new Person(personId, "Fictional Claimable Person");
                var secondPerson = new Person(secondPersonId, "Fictional Second Person");
                store.ReviewPerson(familyId, adminId,
                    store.ProposePerson(familyId, claimantId, firstPerson), approve: true);
                store.ReviewPerson(familyId, adminId,
                    store.ProposePerson(familyId, claimantId, secondPerson), approve: true);
                var grants = new SqliteGraphGrantStore(path);
                grants.GrantPerson(familyId, branchId, adminId, claimantId, personId);
                grants.GrantPerson(familyId, branchId, adminId, claimantId, secondPersonId);
                grants.GrantPerson(familyId, branchId, adminId, reviewerId, personId);
                grants.GrantPerson(familyId, branchId, adminId, reviewerId, secondPersonId);

                var service = new ProfileClaimService(bridge, store);
                var race = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
                {
                    try
                    {
                        return await service.ProposeAsync(Context(claimantCookie),
                            familyId, branchId, personId);
                    }
                    catch (InvalidOperationException)
                    {
                        return null;
                    }
                }));
                Check(race.Count(claim => claim is not null) == 1,
                    "concurrent duplicate proposals persist only one claim");
                var claim = race.Single(item => item is not null)!;
                Check(claim.State == ProfileClaimState.Pending &&
                      claim.PrincipalId == claimantId && claim.PersonId == personId &&
                      claim.FamilyId == familyId && claim.PersonId != claim.PrincipalId,
                    "claim keeps the verified login separate from the genealogical Person");

                await Expect<InvalidOperationException>(() => service.ProposeAsync(
                    Context(reviewerCookie), familyId, branchId, personId),
                    "a second login cannot claim the same person");
                await Expect<InvalidOperationException>(() => service.ProposeAsync(
                    Context(claimantCookie), familyId, branchId, secondPersonId),
                    "a login cannot hold multiple active claims in one family");
                await Expect<UnauthorizedAccessException>(() => service.ProposeAsync(
                    Context(pendingCookie), familyId, branchId, personId),
                    "pending membership cannot claim a person");
                await Expect<UnauthorizedAccessException>(() => service.ProposeAsync(
                    Context(noViewCookie), familyId, branchId, personId),
                    "active membership without person visibility cannot claim");
                await Expect<UnauthorizedAccessException>(() => service.ProposeAsync(
                    Context(claimantCookie), otherFamilyId, branchId, personId),
                    "cross-family claim is denied");
                var forgedContext = Context();
                forgedContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("familytree:trusted:validated", "true")], "forged"));
                await Expect<UnauthorizedAccessException>(() => service.ProposeAsync(
                    forgedContext, familyId, branchId, personId),
                    "caller-injected identity cannot create a claim");
                Check((await service.ListPendingAsync(Context(adminCookie), familyId))
                    .Select(item => item.Id).SequenceEqual([claim.Id]),
                    "active administrator can list pending claims");

                await Expect<UnauthorizedAccessException>(() => service.ReviewAsync(
                    Context(otherAdminCookie), familyId, claim.Id, approve: true),
                    "cross-family administrator cannot review a claim");
                var rejected = await service.ReviewAsync(Context(adminCookie),
                    familyId, claim.Id, approve: false);
                Check(rejected.State == ProfileClaimState.Rejected &&
                      rejected.ReviewedBy == adminId &&
                      rejected.PersonId == claim.PersonId &&
                      rejected.PrincipalId == claim.PrincipalId,
                    "independent rejection preserves immutable claim identity");
                var rejectedAudit = await service.ReadAuditAsync(
                    Context(adminCookie), familyId, claim.Id);
                Check(rejectedAudit.Select(entry => (entry.ActorId, entry.Action))
                    .SequenceEqual([
                        (claimantId, "profile-claim-proposed"),
                        (adminId, "profile-claim-rejected")
                    ]), "proposal and rejection audit exact actors and actions");

                approvedClaim = await service.ProposeAsync(
                    Context(claimantCookie), familyId, branchId, personId);
                approvedClaim = await service.ReviewAsync(Context(reviewerCookie),
                    familyId, approvedClaim.Id, approve: true);
                Check(approvedClaim.State == ProfileClaimState.Approved &&
                      approvedClaim.ReviewedBy == reviewerId &&
                      approvedClaim.PrincipalId == claimantId &&
                      approvedClaim.PersonId == personId,
                    "independent approval preserves immutable claim identity");
                restartPendingClaim = await service.ProposeAsync(
                    Context(reviewerCookie), familyId, branchId, secondPersonId);
            }

            using (var registry = new SqlitePrincipalRegistry(path, identityKey))
            using (var store = new SqliteFamilyStore(path, key))
            {
                var bridge = new VerifiedFamilyMembership(registry, store, [provider]);
                var service = new ProfileClaimService(bridge, store);
                var pending = await service.ListPendingAsync(Context(adminCookie), familyId);
                Check(pending.Select(item => item.Id).SequenceEqual([restartPendingClaim!.Id]),
                    "pending claim survives database restart");
                var approvedAudit = await service.ReadAuditAsync(
                    Context(adminCookie), familyId, approvedClaim!.Id);
                Check(approvedAudit.Select(entry => (entry.ActorId, entry.Action))
                    .SequenceEqual([
                        (claimantId, "profile-claim-proposed"),
                        (reviewerId, "profile-claim-approved")
                    ]), "approved claim and exact audit proof survive restart");
                await Expect<InvalidOperationException>(() => service.ProposeAsync(
                    Context(reviewerCookie), familyId, branchId, personId),
                    "approved person claim remains unique after restart");
                var withdrawn = await service.WithdrawAsync(Context(reviewerCookie),
                    familyId, restartPendingClaim.Id);
                Check(withdrawn.State == ProfileClaimState.Withdrawn,
                    "claim owner can withdraw a pending claim");
                store.RevokeMember(familyId, adminId, claimantId);
                await Expect<UnauthorizedAccessException>(() => service.ProposeAsync(
                    Context(claimantCookie), familyId, branchId, secondPersonId),
                    "revoked membership cannot submit a claim");
                var withdrawalAudit = await service.ReadAuditAsync(
                    Context(adminCookie), familyId, restartPendingClaim.Id);
                Check(withdrawalAudit.Select(entry => (entry.ActorId, entry.Action))
                    .SequenceEqual([
                        (reviewerId, "profile-claim-proposed"),
                        (reviewerId, "profile-claim-withdrawn")
                    ]), "withdrawal audit identifies exact actor and action");
            }

            Check(typeof(ProfileClaimService).GetMethod(nameof(ProfileClaimService.ProposeAsync))!
                    .GetParameters().Select(parameter => parameter.Name)
                    .SequenceEqual(["context", "familyId", "branchId", "personId"]),
                "claim service never accepts a caller-supplied principal ID");
            Console.WriteLine("PASS durable profile claims: cookie identity, transaction authorization, independent review, uniqueness, concurrency, restart and audit");
        }
        finally
        {
            foreach (var scope in scopes) scope.Dispose();
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(identityKey);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Activate(SqliteFamilyStore store, Guid familyId,
        Guid administratorId, Guid memberId)
    {
        var invitation = store.IssueInvitation(familyId, administratorId, memberId,
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
        store.AcceptInvitation(familyId, memberId, invitation, DateTimeOffset.UtcNow);
        store.ApproveMember(familyId, administratorId, memberId);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Expect<T>(Func<Task> action, string message)
        where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
