using System.Security.Claims;
using System.Security.Cryptography;
using FamilyTree.Api;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

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
        using var scopes = new ScopeLifetime();

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
            var noViewId = Guid.Empty;
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
                noViewId = (await bridge.RegisterAsync(Context(noViewCookie)))!.Value;
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
                grants.GrantPerson(familyId, branchId, adminId, noViewId, secondPersonId);

                var service = new ProfileClaimService(bridge, store);
                var raceContexts = new[] { Context(claimantCookie), Context(claimantCookie) };
                using var start = new Barrier(2);
                var race = await Task.WhenAll(raceContexts.Select(context => Task.Run(async () =>
                {
                    if (!start.SignalAndWait(TimeSpan.FromSeconds(30)))
                        throw new InvalidOperationException("Claim race did not start.");
                    try
                    {
                        return await service.ProposeAsync(context,
                            familyId, branchId, personId);
                    }
                    catch (InvalidOperationException error) when (
                        error.Message == "An active profile claim already exists." &&
                        error.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
                    {
                        return null;
                    }
                })));
                Check(race.Count(claim => claim is not null) == 1,
                    "concurrent duplicate proposals persist only one claim");
                var claim = race.Single(item => item is not null)!;
                Check(claim.State == ProfileClaimState.Pending &&
                      claim.PrincipalId == claimantId && claim.PersonId == personId &&
                      claim.FamilyId == familyId && claim.BranchId == branchId &&
                      claim.PersonId != claim.PrincipalId,
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
                Check((await service.ListPendingAsync(Context(adminCookie), familyId)).Count == 0,
                    "administrator without a grant cannot see pending claims");
                foreach (var approve in new[] { false, true })
                {
                    var hidden = await Denied(() => service.ReviewAsync(
                        Context(adminCookie), familyId, claim.Id, approve));
                    var unknown = await Denied(() => service.ReviewAsync(
                        Context(adminCookie), familyId, Guid.NewGuid(), approve));
                    Check(hidden == unknown, "hidden and unknown review outcomes match");
                }
                var hiddenAudit = await Denied(() => service.ReadAuditAsync(
                    Context(adminCookie), familyId, claim.Id));
                var unknownAudit = await Denied(() => service.ReadAuditAsync(
                    Context(adminCookie), familyId, Guid.NewGuid()));
                Check(hiddenAudit == unknownAudit, "hidden and unknown audit outcomes match");
                grants.GrantPerson(familyId, Guid.NewGuid(), adminId, adminId, personId);
                Check((await service.ListPendingAsync(Context(adminCookie), familyId)).Count == 0,
                    "a grant in another branch does not reveal the claim");
                grants.GrantPerson(familyId, branchId, adminId, adminId, personId);
                grants.GrantPerson(familyId, branchId, adminId, adminId, secondPersonId);
                Check((await service.ListPendingAsync(Context(adminCookie), familyId))
                    .Select(item => item.Id).SequenceEqual([claim.Id]),
                    "active administrator can list pending claims");
                var hiddenClaim = await service.ProposeAsync(
                    Context(noViewCookie), familyId, branchId, secondPersonId);
                grants.RevokePerson(familyId, branchId, adminId, adminId, secondPersonId);
                Check((await service.ListPendingAsync(Context(adminCookie), familyId))
                    .Select(item => item.Id).SequenceEqual([claim.Id]),
                    "inbox includes visible claims but excludes hidden claims");
                grants.RevokePerson(familyId, branchId, adminId, noViewId, secondPersonId);
                var deniedWithdrawal = await Denied(() => service.WithdrawAsync(
                    Context(noViewCookie), familyId, hiddenClaim.Id));
                var unknownWithdrawal = await Denied(() => service.WithdrawAsync(
                    Context(noViewCookie), familyId, Guid.NewGuid()));
                Check(deniedWithdrawal == unknownWithdrawal,
                    "hidden and unknown withdrawal outcomes match");
                grants.GrantPerson(familyId, branchId, adminId, adminId, secondPersonId);
                Check((await service.ListPendingAsync(Context(adminCookie), familyId))
                    .Select(item => item.Id).ToHashSet()
                    .SetEquals([claim.Id, hiddenClaim.Id]),
                    "denied withdrawal leaves the pending claim unchanged");
                Check((await service.ReadAuditAsync(Context(adminCookie), familyId,
                    hiddenClaim.Id)).Select(entry => entry.Action)
                    .SequenceEqual(["profile-claim-proposed"]),
                    "denied withdrawal writes no audit event");
                grants.GrantPerson(familyId, branchId, adminId, noViewId, secondPersonId);
                Check((await service.WithdrawAsync(Context(noViewCookie), familyId,
                    hiddenClaim.Id)).State == ProfileClaimState.Withdrawn,
                    "visible claimant can withdraw a pending claim");
                grants.RevokePerson(familyId, branchId, adminId, reviewerId, personId);
                Check((await service.ListPendingAsync(Context(reviewerCookie), familyId)).Count == 0,
                    "revoked reviewer grant hides the claim");
                foreach (var approve in new[] { false, true })
                    await Expect<UnauthorizedAccessException>(() => service.ReviewAsync(
                        Context(reviewerCookie), familyId, claim.Id, approve),
                        "reviewer with revoked grant cannot decide");
                await Expect<UnauthorizedAccessException>(() => service.ReadAuditAsync(
                    Context(reviewerCookie), familyId, claim.Id),
                    "reviewer with revoked grant cannot read audit");
                grants.GrantPerson(familyId, branchId, adminId, reviewerId, personId);
                grants.RevokePerson(familyId, branchId, adminId, claimantId, personId);
                await Expect<UnauthorizedAccessException>(() => service.ReviewAsync(
                    Context(adminCookie), familyId, claim.Id, approve: true),
                    "approval rechecks the claimant grant");
                grants.GrantPerson(familyId, branchId, adminId, claimantId, personId);

                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
                }.ToString()))
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction(deferred: false);
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = """
                        DELETE FROM person_visibility WHERE family_id=$f AND branch_id=$b
                        AND principal_id=$p AND person_id=$person;
                        """;
                    delete.Parameters.AddWithValue("$f", familyId.ToString("D"));
                    delete.Parameters.AddWithValue("$b", branchId.ToString("D"));
                    delete.Parameters.AddWithValue("$p", reviewerId.ToString("D"));
                    delete.Parameters.AddWithValue("$person", personId.ToString("D"));
                    Check(delete.ExecuteNonQuery() == 1, "race fixture revokes reviewer grant");
                    var reviewContext = Context(reviewerCookie);
                    using var started = new ManualResetEventSlim();
                    var racingReview = Task.Run(async () =>
                    {
                        started.Set();
                        return await Denied(() => service.ReviewAsync(
                            reviewContext, familyId, claim.Id, approve: true));
                    });
                    Check(started.Wait(TimeSpan.FromSeconds(10)), "review race started");
                    await Task.Delay(150);
                    Check(!racingReview.IsCompleted, "review waits for the grant transaction");
                    transaction.Commit();
                    Check((await racingReview).Message == "Access denied.",
                        "review observes the committed grant revocation");
                }
                Check((await service.ListPendingAsync(Context(adminCookie), familyId))
                    .Any(item => item.Id == claim.Id), "denied racing review leaves claim pending");
                Check((await service.ReadAuditAsync(Context(adminCookie), familyId, claim.Id))
                    .Select(item => item.Action).SequenceEqual(["profile-claim-proposed"]),
                    "denied racing review writes no decision audit");
                grants.GrantPerson(familyId, branchId, adminId, reviewerId, personId);

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
                await Expect<UnauthorizedAccessException>(() => service.ReviewAsync(
                    Context(reviewerCookie), familyId, restartPendingClaim.Id, approve: false),
                    "a claimant cannot reject their own claim");
            }

            using (var registry = new SqlitePrincipalRegistry(path, identityKey))
            using (var store = new SqliteFamilyStore(path, key))
            {
                var bridge = new VerifiedFamilyMembership(registry, store, [provider]);
                var service = new ProfileClaimService(bridge, store);
                var pending = await service.ListPendingAsync(Context(adminCookie), familyId);
                Check(pending.Select(item => item.Id).SequenceEqual([restartPendingClaim!.Id]),
                    "pending claim survives database restart");
                var originalEnvelope = ReadClaimEnvelope(path, restartPendingClaim.Id);
                var tamperedEnvelope = (byte[])originalEnvelope.Clone();
                tamperedEnvelope[0] ^= 0xFF;
                WriteClaimEnvelope(path, restartPendingClaim.Id, tamperedEnvelope);
                await Expect<InvalidDataException>(() => service.ListPendingAsync(
                    Context(adminCookie), familyId),
                    "tampered claim identity is rejected");
                await Expect<InvalidDataException>(() => service.ReviewAsync(
                    Context(adminCookie), familyId, restartPendingClaim.Id, approve: false),
                    "tampered claim cannot be reviewed");
                await Expect<InvalidDataException>(() => service.ReadAuditAsync(
                    Context(adminCookie), familyId, restartPendingClaim.Id),
                    "tampered claim cannot expose audit data");
                WriteClaimEnvelope(path, restartPendingClaim.Id, originalEnvelope);
                Check((await service.ListPendingAsync(Context(adminCookie), familyId))
                    .Single().Id == restartPendingClaim.Id,
                    "restoring the authenticated claim envelope preserves the pending claim");
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
                var revokedPendingClaim = await service.ProposeAsync(
                    Context(noViewCookie), familyId, branchId, secondPersonId);
                store.RevokeMember(familyId, adminId, noViewId);
                await Expect<UnauthorizedAccessException>(() => service.ReviewAsync(
                    Context(adminCookie), familyId, revokedPendingClaim.Id, approve: true),
                    "a revoked claimant's pending claim cannot be approved");
                Check((await service.ReadAuditAsync(Context(adminCookie), familyId,
                    revokedPendingClaim.Id)).Select(entry => entry.Action)
                    .SequenceEqual(["profile-claim-proposed"]),
                    "denied review after revocation writes no decision audit");
                var staleRejection = await service.ReviewAsync(Context(adminCookie),
                    familyId, revokedPendingClaim.Id, approve: false);
                Check(staleRejection.State == ProfileClaimState.Rejected &&
                      staleRejection.ReviewedBy == adminId,
                    "independent reviewer can reject a revoked claimant");
                Check((await service.ReadAuditAsync(Context(adminCookie), familyId,
                    revokedPendingClaim.Id)).Select(entry => (entry.ActorId, entry.Action))
                    .SequenceEqual([(noViewId, "profile-claim-proposed"),
                        (adminId, "profile-claim-rejected")]),
                    "stale rejection is audited exactly once");
                var replacement = await service.ProposeAsync(
                    Context(reviewerCookie), familyId, branchId, secondPersonId);
                Check(replacement.State == ProfileClaimState.Pending,
                    "rejecting the stale claim releases the person reservation");
                store.RevokeMember(familyId, adminId, reviewerId);
                await Expect<UnauthorizedAccessException>(() => service.ListPendingAsync(
                    Context(reviewerCookie), familyId), "revoked reviewer cannot access inbox");
                await Expect<UnauthorizedAccessException>(() => service.ReviewAsync(
                    Context(reviewerCookie), familyId, replacement.Id, approve: false),
                    "revoked reviewer cannot decide a claim");
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

    private static byte[] ReadClaimEnvelope(string path, Guid claimId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT identity_envelope FROM profile_claims WHERE id=$id;";
        command.Parameters.AddWithValue("$id", claimId.ToString("D"));
        return (byte[])command.ExecuteScalar()!;
    }

    private static void WriteClaimEnvelope(string path, Guid claimId, byte[] envelope)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE profile_claims SET identity_envelope=$envelope WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$envelope", envelope);
        command.Parameters.AddWithValue("$id", claimId.ToString("D"));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Profile claim fixture unavailable.");
    }

    private static async Task Expect<T>(Func<Task> action, string message)
        where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task<(Type Type, string Message)> Denied(Func<Task> action)
    {
        try { await action(); }
        catch (UnauthorizedAccessException error) { return (error.GetType(), error.Message); }
        throw new InvalidOperationException("Expected access denial.");
    }

    private sealed class ScopeLifetime : IDisposable
    {
        private readonly List<IServiceScope> _scopes = [];

        internal void Add(IServiceScope scope) => _scopes.Add(scope);

        public void Dispose()
        {
            foreach (var scope in _scopes)
                scope.Dispose();
            _scopes.Clear();
        }
    }
}
