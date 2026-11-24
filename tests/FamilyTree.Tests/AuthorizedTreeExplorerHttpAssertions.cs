using System.Net;
using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

internal static class AuthorizedTreeExplorerHttpAssertions
{
    private sealed class Source(EncryptedLocalSnapshotStore store, Guid viewer,
        Guid family, Guid branch, BranchVisibilityGrant grant, FieldPrivacyPolicy fields)
        : ITrustedTreeReadSource
    {
        public async Task<TrustedTreeReadSnapshot?> LoadAsync(Guid principal, Guid tenant,
            Guid scope, CancellationToken token)
        {
            if (principal != viewer || tenant != family || scope != branch) return null;
            var snapshot = await store.LoadAsync(token);
            return new(viewer, snapshot.FamilyId, branch, true,
                snapshot.People, snapshot.Relationships, [grant], fields);
        }
    }

    private sealed class Reader(PrivateTreeReadService service, Guid viewer, Guid family, Guid branch)
        : IAuthorizedTreeReader
    {
        public Task<AuthorizedTreeProjection?> ReadAsync(Guid center, TreeOptions options,
            CancellationToken token) => service.GetAsync(viewer, family, branch, center, options, token);
    }

    internal static async Task RunAsync()
    {
        var path = Path.Join(Path.GetTempPath(), "familytree-ui-" + Guid.NewGuid().ToString("N"));
        var viewer = Guid.NewGuid();
        var family = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var center = new Person(Guid.NewGuid(), "Fictional Center");
        var child = new Person(Guid.NewGuid(), "Fictional Child");
        var spouse = new Person(Guid.NewGuid(), "Fictional Partner");
        var friend = new Person(Guid.NewGuid(), "Fictional Friend");
        var hidden = new Person(Guid.NewGuid(), "Fictional Hidden");
        var parent = new Person(Guid.NewGuid(), "Fictional Parent");
        var sibling = new Person(Guid.NewGuid(), "Fictional Sibling");
        var parentEdge = Relationship.Create(center.Id, child.Id, RelationKind.Parent);
        var ancestorEdge = Relationship.Create(parent.Id, center.Id, RelationKind.Parent);
        var siblingEdge = Relationship.Create(parent.Id, sibling.Id, RelationKind.Parent);
        var spouseEdge = Relationship.Create(center.Id, spouse.Id, RelationKind.Spouse);
        var friendEdge = Relationship.Create(center.Id, friend.Id, RelationKind.Friend);
        var hiddenEdge = Relationship.Create(center.Id, hidden.Id, RelationKind.Parent);
        var deniedEdge = Relationship.Create(child.Id, spouse.Id, RelationKind.Mentor);
        var grant = new BranchVisibilityGrant(viewer, family, branch, MembershipState.Active,
            new HashSet<Guid> { center.Id, child.Id, spouse.Id, friend.Id, hidden.Id, parent.Id, sibling.Id },
            new HashSet<Guid> { parentEdge.Id, ancestorEdge.Id, siblingEdge.Id, spouseEdge.Id, friendEdge.Id, hiddenEdge.Id });
        var fields = new FieldPrivacyPolicy(family,
            new[] { center, child, spouse, friend, parent, sibling }.Select(p => new FamilyFieldGrant(
                viewer, family, p.Id, MembershipState.Active,
                new HashSet<ProfileField> { ProfileField.DisplayName })).ToArray());
        try
        {
            using var store = new EncryptedLocalSnapshotStore(path, RandomNumberGenerator.GetBytes(32));
            await store.SaveAsync(new LocalFamilySnapshot(family, 1,
                [center, child, spouse, friend, hidden, parent, sibling],
                [parentEdge, ancestorEdge, siblingEdge, spouseEdge, friendEdge, hiddenEdge, deniedEdge]));
            var reader = new Reader(new PrivateTreeReadService(
                new Source(store, viewer, family, branch, grant, fields)), viewer, family, branch);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.MapGet("/test-tree", (Delegate)(Func<HttpContext, Task<IResult>>)
                (http => AuthorizedTreeExplorer.ShowAsync(http, reader)));
            await app.StartAsync();
            using var client = app.GetTestClient();
            var baseUrl = "/test-tree?center=" + center.Id;
            using var initial = await client.GetAsync(baseUrl);
            var body = await initial.Content.ReadAsStringAsync();
            Check(initial.StatusCode == HttpStatusCode.OK &&
                initial.Headers.CacheControl?.NoStore == true &&
                initial.Headers.GetValues("Referrer-Policy").Single() == "no-referrer",
                "authorized response is non-cacheable and suppresses referrers");
            Check(body.Contains("Fictional Child", StringComparison.Ordinal) &&
                body.Contains("Fictional Partner", StringComparison.Ordinal) &&
                !body.Contains("Fictional Friend", StringComparison.Ordinal) &&
                !body.Contains("Fictional Hidden", StringComparison.Ordinal) &&
                !body.Contains(hidden.Id.ToString(), StringComparison.Ordinal) &&
                !body.Contains("Mentor connection", StringComparison.Ordinal),
                "only authorized nodes and edges enter markup with default toggles");
            Check(body.Contains("name='above' type='number' min='0' max='8' value='4'", StringComparison.Ordinal) &&
                body.Contains("name='below' type='number' min='0' max='8' value='4'", StringComparison.Ordinal) &&
                body.Contains("tabindex='0'", StringComparison.Ordinal),
                "default depth and keyboard navigation rendered");

            var options = "&above=0&below=8&spouses=no&siblings=no&social=yes";
            using var changed = await client.GetAsync(baseUrl + options);
            var changedBody = await changed.Content.ReadAsStringAsync();
            Check(changedBody.Contains("Fictional Friend", StringComparison.Ordinal) &&
                !changedBody.Contains("Fictional Partner", StringComparison.Ordinal) &&
                !changedBody.Contains("Fictional Sibling", StringComparison.Ordinal) &&
                changedBody.Contains("&amp;above=0&amp;below=8&amp;spouses=no&amp;siblings=no&amp;social=yes", StringComparison.Ordinal),
                "depth boundaries and toggles flow into projection and recenter links");
            using var siblingView = await client.GetAsync(baseUrl + "&above=0&below=0&spouses=no&siblings=yes");
            var siblingBody = await siblingView.Content.ReadAsStringAsync();
            Check(siblingBody.Contains("Fictional Sibling", StringComparison.Ordinal) &&
                !siblingBody.Contains("Fictional Parent", StringComparison.Ordinal) &&
                !siblingBody.Contains(parent.Id.ToString(), StringComparison.Ordinal),
                "sibling toggle works without disclosing the out-of-depth parent");
            using var recentered = await client.GetAsync("/test-tree?center=" + child.Id + options);
            var recenteredBody = await recentered.Content.ReadAsStringAsync();
            Check(recenteredBody.Contains("value='" + child.Id + "' selected", StringComparison.Ordinal),
                "selected authorized center persists on reload");
            using var reverse = await client.GetAsync("/test-tree?center=" + child.Id + "&above=8&below=0");
            Check((await reverse.Content.ReadAsStringAsync()).Contains("Fictional Center", StringComparison.Ordinal),
                "ancestor and descendant depths are independent");
            using var searched = await client.GetAsync(baseUrl + "&search=Child");
            var searchedBody = await searched.Content.ReadAsStringAsync();
            Check(searchedBody.Contains(">Center on Fictional Child</a></td>", StringComparison.Ordinal) &&
                !searchedBody.Contains(">Center on Fictional Partner</a></td>", StringComparison.Ordinal),
                "search filters only the already authorized visible list");
            using var denied = await client.GetAsync("/test-tree?center=" + hidden.Id);
            using var unknown = await client.GetAsync("/test-tree?center=" + Guid.NewGuid());
            Check(denied.StatusCode == HttpStatusCode.NotFound && unknown.StatusCode == denied.StatusCode &&
                await denied.Content.ReadAsStringAsync() == await unknown.Content.ReadAsStringAsync(),
                "forbidden and unknown centers have identical responses");
            await app.StopAsync();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
        Console.WriteLine("PASS authorized tree UI with persisted synthetic data and privacy filtering");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
