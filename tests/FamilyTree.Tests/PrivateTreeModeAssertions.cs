using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyTree.Domain;
using FamilyTree.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class PrivateTreeModeAssertions
{
    private sealed class LocalApiHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? SessionCookie { get; private set; }
        public List<string?> SessionCookies { get; } = [];
        public List<string?> RequestCookies { get; } = [];
        public List<Uri> RequestUris { get; } = [];
        public List<string> MembershipCalls { get; } = [];
        public List<string?> MembershipBodies { get; } = [];
        public string? ExpectedSessionCookie { get; set; }
        public bool Authenticated { get; set; }
        public HttpStatusCode RegistrationStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode MembershipStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode SearchStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode TreeStatus { get; set; } = HttpStatusCode.OK;
        public string CapabilitiesResponse { get; set; } =
            """{"membershipActions":["invite","accept","approve","revoke"]}""";
        public bool ThrowOnTree { get; init; }

        public static Guid FamilyId { get; } = Guid.NewGuid();
        public static Guid BranchId { get; } = Guid.NewGuid();
        public static Guid CenterId { get; } = Guid.NewGuid();
        public static Guid ParentId { get; } = Guid.NewGuid();
        public static Guid ChildId { get; } = Guid.NewGuid();
        public static Guid SpouseId { get; } = Guid.NewGuid();
        public static Guid SiblingId { get; } = Guid.NewGuid();
        public static Guid SocialId { get; } = Guid.NewGuid();
        public static Guid HiddenId { get; } = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var uri = request.RequestUri ??
                throw new InvalidOperationException("API request URI missing.");
            RequestUris.Add(uri);
            var requestCookie = request.Headers.TryGetValues("Cookie", out var requestCookies)
                ? requestCookies.Single()
                : null;
            RequestCookies.Add(requestCookie);
            if (request.RequestUri?.AbsolutePath == "/auth/providers")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"providers":["google","microsoft","unknown"]}"""));
            if (request.RequestUri?.AbsolutePath == "/auth/session")
            {
                SessionCookie = request.Headers.TryGetValues("Cookie", out var values)
                    ? values.Single()
                    : null;
                SessionCookies.Add(SessionCookie);
                return Task.FromResult(Authenticated
                    ? Json(HttpStatusCode.OK,
                        """{"authenticated":true,"familyAccess":false}""")
                    : Json(HttpStatusCode.Unauthorized, ""));
            }
            if (uri.AbsolutePath == "/api/v1/capabilities")
                return Task.FromResult(Json(HttpStatusCode.OK, CapabilitiesResponse));
            if (uri.AbsolutePath == "/api/v1/csrf")
            {
                if (requestCookie != ExpectedSessionCookie)
                    throw new InvalidOperationException("API CSRF request did not forward the verified session cookie.");
                var response = Json(HttpStatusCode.OK,
                    """{"requestToken":"synthetic-api-csrf-token"}""");
                response.Headers.TryAddWithoutValidation("Set-Cookie",
                    "__Host-FamilyTreeAntiforgery=synthetic-api-csrf-cookie; Path=/; Secure; HttpOnly; SameSite=Strict");
                return Task.FromResult(response);
            }
            if (uri.AbsolutePath == "/api/v1/me")
            {
                if (ExpectedSessionCookie is null ||
                    requestCookie is null ||
                    !requestCookie.StartsWith(ExpectedSessionCookie + "; ", StringComparison.Ordinal) ||
                    !requestCookie.Contains("__Host-FamilyTreeAntiforgery=synthetic-api-csrf-cookie",
                        StringComparison.Ordinal) ||
                    !request.Headers.TryGetValues("Origin", out var origins) ||
                    origins.Single() != "https://localhost:5043" ||
                    !request.Headers.TryGetValues("X-CSRF-TOKEN", out var tokens) ||
                    tokens.Single() != "synthetic-api-csrf-token")
                    throw new InvalidOperationException("API identity registration did not use its same-origin antiforgery contract.");
                return Task.FromResult(RegistrationStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK,
                        """{"principalId":"21829b29-6bf7-4e31-b983-9fb54b76aa49"}""")
                    : Json(RegistrationStatus, ""));
            }

            var membershipPrefix = $"/api/v1/families/{FamilyId:D}/";
            if (uri.AbsolutePath == membershipPrefix + "invitations" ||
                uri.AbsolutePath.EndsWith("/accept", StringComparison.Ordinal) ||
                uri.AbsolutePath.EndsWith("/approve", StringComparison.Ordinal) ||
                uri.AbsolutePath.StartsWith(membershipPrefix + "members/", StringComparison.Ordinal))
            {
                if (ExpectedSessionCookie is null ||
                    requestCookie != ExpectedSessionCookie +
                        "; __Host-FamilyTreeAntiforgery=synthetic-api-csrf-cookie" ||
                    !request.Headers.TryGetValues("Origin", out var origins) ||
                    origins.Single() != "https://localhost:5043" ||
                    !request.Headers.TryGetValues("X-CSRF-TOKEN", out var tokens) ||
                    tokens.Single() != "synthetic-api-csrf-token")
                    throw new InvalidOperationException("Membership request did not use the protected same-origin antiforgery contract.");
                var body = request.Content?.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter().GetResult();
                MembershipCalls.Add(request.Method + " " + uri.AbsolutePath);
                MembershipBodies.Add(body);
                if (MembershipStatus != HttpStatusCode.OK)
                    return Task.FromResult(Json(MembershipStatus, ""));
                return Task.FromResult(uri.AbsolutePath == membershipPrefix + "invitations"
                    ? Json(HttpStatusCode.OK,
                        """{"invitationId":"19a21fa7-2302-40eb-8f5e-7472ef64d0d5"}""")
                    : CreateNoContentResponse());
            }

            var familyPrefix = $"/api/v1/families/{FamilyId:D}/branches/{BranchId:D}/";
            if (uri.AbsolutePath == familyPrefix + "search")
                return Task.FromResult(SearchStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new[]
                    {
                        new AuthorizedPersonSummary(CenterId, "Fictional Center"),
                        new AuthorizedPersonSummary(ChildId, "Fictional <script>Guest</script>")
                    }))
                    : Json(SearchStatus, ""));
            if (uri.AbsolutePath.StartsWith(familyPrefix + "tree/", StringComparison.Ordinal))
            {
                if (ThrowOnTree)
                    return Task.FromException<HttpResponseMessage>(
                        new HttpRequestException("PRIVATE_EXCEPTION_SENTINEL"));
                return Task.FromResult(TreeStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new AuthorizedTreeProjection(
                        CenterId,
                        [
                            new AuthorizedTreeNode(CenterId, "Fictional Center", 0),
                            new AuthorizedTreeNode(ParentId, "Fictional Parent", -1),
                            new AuthorizedTreeNode(ChildId, "Fictional Child", 1),
                            new AuthorizedTreeNode(SpouseId, "Fictional Spouse", 0),
                            new AuthorizedTreeNode(SiblingId, "Fictional Sibling", 0),
                            new AuthorizedTreeNode(SocialId, "Fictional Social Contact", 0)
                        ],
                        [
                            new AuthorizedTreeEdge(Guid.NewGuid(), ParentId, CenterId, RelationKind.Parent),
                            new AuthorizedTreeEdge(Guid.NewGuid(), CenterId, ChildId, RelationKind.Parent),
                            new AuthorizedTreeEdge(Guid.NewGuid(), CenterId, SpouseId, RelationKind.Spouse),
                            new AuthorizedTreeEdge(Guid.NewGuid(), CenterId, SocialId, RelationKind.Friend)
                        ],
                        [new InferredSiblingConnection(CenterId, SiblingId)])))
                    : Json(TreeStatus, ""));
            }
            throw new InvalidOperationException("Unexpected API route.");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage CreateNoContentResponse() =>
            new(HttpStatusCode.NoContent);
    }

    private sealed class SyntheticAuthorization(HashSet<Guid> visible) : IFamilyAuthorization
    {
        public Task<bool> CanSeePersonAsync(
            Guid viewerId, Guid personId, CancellationToken cancellationToken = default) =>
            Task.FromResult(visible.Contains(personId));

        public Task<bool> CanSeeRelationshipAsync(
            Guid viewerId, Relationship relationship, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    public static async Task RunAsync(string verifiedSessionCookie)
    {
        PrivateTreeUiAssertions.Run();
        var defaults = PrivateTreeModeOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Ensure(!defaults.Enabled && defaults.ApiBaseUri is null, "private mode is disabled by default");

        var invalidValues = new[]
        {
            "http://localhost:5043",
            "https://api.example.test",
            "https://user@localhost:5043",
            "https://localhost:5043/path"
        };
        foreach (var configuration in invalidValues.Select(CreateUnsafeApiConfiguration))
        {
            var rejected = false;
            try
            {
                PrivateTreeModeOptions.FromConfiguration(configuration);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("FAMILYTREE_API_BASE_URL", StringComparison.Ordinal))
            {
                rejected = true;
            }
            Ensure(rejected, "unsafe API base URL is rejected");
        }

        await VerifyPrivateRouteAsync();
        await VerifyAuthenticatedWorkspaceAsync(verifiedSessionCookie);
        VerifyDepthLimits();
        Console.WriteLine("PASS private web mode and deep synthetic traversal assertions");
    }

    private static async Task VerifyPrivateRouteAsync()
    {
        var handler = new LocalApiHandler();
        using var apiHttpClient = new HttpClient(handler);
        var options = new PrivateTreeModeOptions(true, new Uri("https://localhost:5043/"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = "FamilyTreeWebAntiforgery";
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(apiHttpClient);
        builder.Services.AddSingleton<PrivateTreeApiClient>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        PrivateTreeRoutes.Map(app);
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            using var response = await client.GetAsync("https://localhost/private");
            var html = await response.Content.ReadAsStringAsync();
            Ensure(response.StatusCode == HttpStatusCode.OK &&
                response.Headers.CacheControl?.NoStore == true &&
                response.Headers.GetValues("Referrer-Policy").Single() == "no-referrer",
                "private sign-in response is non-cacheable and suppresses referrers");
            Ensure(html.Contains("Sign in with Google", StringComparison.Ordinal) &&
                html.Contains("Sign in with Microsoft", StringComparison.Ordinal) &&
                !html.Contains("unknown", StringComparison.Ordinal) &&
                !html.Contains("not-a-real-ticket", StringComparison.Ordinal),
                "only configured identity providers are linked without exposing API or cookie data");
            Ensure(handler.Requests == 2 && handler.SessionCookie is null,
                "unauthenticated browser requests do not forward unrelated cookies");

            using var spoofed = new HttpRequestMessage(HttpMethod.Get, "https://localhost/private");
            spoofed.Headers.TryAddWithoutValidation("Cookie",
                "theme=neutral; __Host-FamilyTreeSession=not-a-real-ticket");
            using var rejected = await client.SendAsync(spoofed);
            var rejectedHtml = await rejected.Content.ReadAsStringAsync();
            Ensure(rejected.StatusCode == HttpStatusCode.OK &&
                handler.SessionCookies.LastOrDefault() == "__Host-FamilyTreeSession=not-a-real-ticket" &&
                rejectedHtml.Contains("Sign in with Google", StringComparison.Ordinal),
                "an arbitrary browser cookie is forwarded only to the API and cannot create an authenticated session");

            var requestsBeforePublicHost = handler.Requests;
            using var publicHost = await client.GetAsync("https://example.test/private");
            Ensure(publicHost.StatusCode == HttpStatusCode.NotFound &&
                handler.Requests == requestsBeforePublicHost,
                "private mode rejects non-local hosts before contacting the API");

            using var insecure = await client.GetAsync("http://localhost/private");
            var insecureHtml = await insecure.Content.ReadAsStringAsync();
            Ensure(insecure.StatusCode == HttpStatusCode.OK &&
                insecure.Headers.CacheControl?.NoStore == true &&
                insecureHtml.Contains("could not be reached securely", StringComparison.Ordinal) &&
                handler.Requests == requestsBeforePublicHost,
                "insecure requests never contact the API");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        var disabledBuilder = WebApplication.CreateBuilder();
        disabledBuilder.WebHost.UseTestServer();
        disabledBuilder.Services.AddAntiforgery();
        using var disabledApiHttpClient = new HttpClient(new LocalApiHandler());
        disabledBuilder.Services.AddSingleton(new PrivateTreeModeOptions(false, null));
        disabledBuilder.Services.AddSingleton(disabledApiHttpClient);
        disabledBuilder.Services.AddSingleton<PrivateTreeApiClient>();
        var disabledApp = disabledBuilder.Build();
        disabledApp.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        PrivateTreeRoutes.Map(disabledApp);
        await disabledApp.StartAsync();
        try
        {
            using var response = await disabledApp.GetTestClient().GetAsync("http://localhost/private");
            Ensure(response.StatusCode == HttpStatusCode.NotFound,
                "disabled private mode does not expose a sign-in route");
        }
        finally
        {
            await disabledApp.StopAsync();
            await disabledApp.DisposeAsync();
        }
    }

    private static void VerifyDepthLimits()
    {
        var people = Enumerable.Range(0, 17)
            .Select(index => new Person(Guid.NewGuid(), $"Synthetic {index}"))
            .ToArray();
        var relationships = new List<Relationship>();
        for (var index = 0; index < 8; index++)
        {
            relationships.Add(Relationship.Create(
                people[index].Id, people[index + 1].Id, RelationKind.Parent));
            relationships.Add(Relationship.Create(
                people[index + 8].Id, people[index + 9].Id, RelationKind.Parent));
        }
        var center = people[8];
        var authorization = new SyntheticAuthorization(people.Select(x => x.Id).ToHashSet());

        var depthZero = PersonTreeProjector.ProjectAsync(Guid.NewGuid(), center.Id,
            people, relationships, new TreeOptions(0, 0, false, false, false),
            authorization).GetAwaiter().GetResult();
        Ensure(depthZero?.Nodes.Count == 1, "depth zero includes only the center");

        var depthOne = PersonTreeProjector.ProjectAsync(Guid.NewGuid(), center.Id,
            people, relationships, new TreeOptions(1, 1, false, false, false),
            authorization).GetAwaiter().GetResult();
        var verifiedDepthOne = depthOne ??
            throw new InvalidOperationException("depth one projection should include the center");
        Ensure(verifiedDepthOne.Nodes.Count == 3 &&
            verifiedDepthOne.Nodes.Min(x => x.Generation) == -1 &&
            verifiedDepthOne.Nodes.Max(x => x.Generation) == 1,
            "depth one includes one ancestor and descendant");

        var depthEight = PersonTreeProjector.ProjectAsync(Guid.NewGuid(), center.Id,
            people, relationships, new TreeOptions(8, 8, false, false, false),
            authorization).GetAwaiter().GetResult();
        var verifiedDepthEight = depthEight ??
            throw new InvalidOperationException("depth eight projection should include the center");
        Ensure(verifiedDepthEight.Nodes.Count == 17 &&
            verifiedDepthEight.Nodes.Min(x => x.Generation) == -8 &&
            verifiedDepthEight.Nodes.Max(x => x.Generation) == 8,
            "depth eight traverses the complete synthetic graph without truncation");
    }

    private static async Task VerifyAuthenticatedWorkspaceAsync(string verifiedSessionCookie)
        {
            var clientHandler = new LocalApiHandler
            {
                Authenticated = true,
                ExpectedSessionCookie = verifiedSessionCookie
            };
            using var apiHttpClient = new HttpClient(clientHandler);
            var options = new PrivateTreeModeOptions(true, new Uri("https://localhost:5043/"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddAntiforgery(antiforgery =>
            {
                antiforgery.Cookie.Name = "FamilyTreeWebAntiforgery";
                antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            });
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(apiHttpClient);
            builder.Services.AddSingleton<PrivateTreeApiClient>();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(context);
            });
            PrivateTreeRoutes.Map(app);
            await app.StartAsync();
            try
            {
                using var client = app.GetTestClient();
                using var initial = await GetPrivateAsync(client, verifiedSessionCookie);
                var initialHtml = await initial.Content.ReadAsStringAsync();
                var antiCookie = GetCookie(initial, "FamilyTreeWebAntiforgery");
                var requestToken = GetFormToken(initialHtml);
                Ensure(initialHtml.Contains("Select family and branch", StringComparison.Ordinal) &&
                    initialHtml.Contains("Open family and branch", StringComparison.Ordinal) &&
                    !initialHtml.Contains("Fictional Center", StringComparison.Ordinal) &&
                    initial.Headers.CacheControl?.NoStore == true,
                    "authenticated session shows a scoped selector without fetching or exposing records");

                var scope = new Dictionary<string, string>
                {
                    ["action"] = "open",
                    ["familyId"] = LocalApiHandler.FamilyId.ToString("D"),
                    ["branchId"] = LocalApiHandler.BranchId.ToString("D"),
                    ["above"] = "4",
                    ["below"] = "4"
                };
                using var opened = await PostAsync(client, scope, requestToken, antiCookie,
                    verifiedSessionCookie);
                var openedHtml = await opened.Content.ReadAsStringAsync();
                Ensure(opened.StatusCode == HttpStatusCode.OK &&
                    openedHtml.Contains("Search visible people", StringComparison.Ordinal) &&
                    openedHtml.Contains("Membership controls", StringComparison.Ordinal) &&
                    openedHtml.Contains("Spouses and partners", StringComparison.Ordinal) &&
                    openedHtml.Contains("Family and branch identifiers are entered manually; access is determined by the API.",
                        StringComparison.Ordinal) &&
                    !openedHtml.Contains("Fictional Center", StringComparison.Ordinal),
                    "selected family and branch show controls but do not imply API-granted records");

                clientHandler.RegistrationStatus = HttpStatusCode.NotFound;
                var unregisteredSearch = new Dictionary<string, string>(scope)
                {
                    ["action"] = "search",
                    ["query"] = "Fictional"
                };
                var readsBeforeUnregisteredSearch = clientHandler.RequestUris.Count;
                using var unregistered = await PostAsync(client, unregisteredSearch,
                    requestToken, antiCookie, verifiedSessionCookie);
                var unregisteredHtml = await unregistered.Content.ReadAsStringAsync();
                Ensure(unregisteredHtml.Contains("Private records are unavailable",
                        StringComparison.Ordinal) &&
                    !unregisteredHtml.Contains("Fictional Center", StringComparison.Ordinal) &&
                    !clientHandler.RequestUris.Skip(readsBeforeUnregisteredSearch)
                        .Any(uri => uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)),
                    "unavailable verified-identity registration fails closed before private reads");

                var writesBeforeUnregisteredMembership = clientHandler.MembershipCalls.Count;
                var unregisteredInvite = MembershipForm(
                    "invite", Guid.NewGuid());
                using var unregisteredMembership = await PostAsync(client, unregisteredInvite,
                    requestToken, antiCookie, verifiedSessionCookie);
                var unregisteredMembershipHtml =
                    await unregisteredMembership.Content.ReadAsStringAsync();
                Ensure(unregisteredMembershipHtml.Contains("Membership request unavailable",
                        StringComparison.Ordinal) &&
                    clientHandler.MembershipCalls.Count == writesBeforeUnregisteredMembership,
                    "membership action stops before writes when verified identity registration fails");
                clientHandler.RegistrationStatus = HttpStatusCode.OK;

                var search = new Dictionary<string, string>(scope)
                {
                    ["action"] = "search",
                    ["query"] = "Fictional"
                };
                using var searched = await PostAsync(client, search, requestToken, antiCookie,
                    verifiedSessionCookie);
                var searchHtml = await searched.Content.ReadAsStringAsync();
                Ensure(searched.StatusCode == HttpStatusCode.OK &&
                    searchHtml.Contains("Fictional Center", StringComparison.Ordinal) &&
                    searchHtml.Contains("Fictional &lt;script&gt;Guest&lt;/script&gt;", StringComparison.Ordinal) &&
                    !searchHtml.Contains("<script>Guest</script>", StringComparison.Ordinal) &&
                    searchHtml.Contains("<button type='submit'>Center on Fictional Center</button>", StringComparison.Ordinal) &&
                    searched.RequestMessage?.RequestUri?.Query.Length == 0,
                    "real API search contract results are rendered as encoded HTML and recenter without URL state");
                var searchApiUri = clientHandler.RequestUris.Single(uri =>
                    uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal));
                Ensure(searchApiUri.Query == "?q=Fictional" &&
                    clientHandler.SessionCookies.LastOrDefault() == verifiedSessionCookie &&
                    clientHandler.RequestCookies.Any(cookie =>
                        cookie == verifiedSessionCookie + "; __Host-FamilyTreeAntiforgery=synthetic-api-csrf-cookie"),
                    "search uses only the verified server-forwarded session cookie and API antiforgery cookie");

                var treePost = new Dictionary<string, string>
                {
                    ["action"] = "tree",
                    ["familyId"] = LocalApiHandler.FamilyId.ToString("D"),
                    ["branchId"] = LocalApiHandler.BranchId.ToString("D"),
                    ["centerId"] = LocalApiHandler.CenterId.ToString("D"),
                    ["above"] = "0",
                    ["below"] = "1"
                };
                using var treeResponse = await PostAsync(client, treePost, requestToken, antiCookie,
                    verifiedSessionCookie);
                var treeHtml = await treeResponse.Content.ReadAsStringAsync();
                Ensure(treeHtml.Contains("Fictional Center", StringComparison.Ordinal) &&
                    treeHtml.Contains("Fictional Child", StringComparison.Ordinal) &&
                    treeHtml.Contains("Fictional Spouse", StringComparison.Ordinal) &&
                    treeHtml.Contains("Fictional Sibling", StringComparison.Ordinal) &&
                    !treeHtml.Contains("Fictional Social Contact", StringComparison.Ordinal) &&
                    !treeHtml.Contains("Fictional Parent", StringComparison.Ordinal) &&
                    treeHtml.Contains("Generations above", StringComparison.Ordinal) &&
                    treeHtml.Contains("Generations below", StringComparison.Ordinal) &&
                    treeHtml.Contains("Authorized people in the selected generations", StringComparison.Ordinal) &&
                    !treeHtml.Contains(LocalApiHandler.HiddenId.ToString("D"), StringComparison.Ordinal),
                    "tree route renders only authorized nodes within selected depth with keyboard recenter forms");

                var toggledTreePost = new Dictionary<string, string>(treePost)
                {
                    ["above"] = "4",
                    ["spouses"] = "false",
                    ["siblings"] = "false",
                    ["social"] = "false"
                };
                using var toggledTree = await PostAsync(client, toggledTreePost,
                    requestToken, antiCookie, verifiedSessionCookie);
                var toggledTreeHtml = await toggledTree.Content.ReadAsStringAsync();
                Ensure(toggledTreeHtml.Contains("Fictional Parent", StringComparison.Ordinal) &&
                    toggledTreeHtml.Contains("Fictional Child", StringComparison.Ordinal) &&
                    !toggledTreeHtml.Contains("Fictional Spouse", StringComparison.Ordinal) &&
                    !toggledTreeHtml.Contains("Fictional Sibling", StringComparison.Ordinal) &&
                    !toggledTreeHtml.Contains("Fictional Social Contact", StringComparison.Ordinal),
                    "relationship toggles limit rendered nodes and edges to the authorized API projection");
                Ensure(clientHandler.RequestUris.Any(uri =>
                    uri.AbsolutePath.EndsWith("/tree/" + LocalApiHandler.CenterId.ToString("D"),
                        StringComparison.Ordinal)),
                    "center-tree request uses the API family/branch route contract");

                await VerifyIndistinguishableDenialsAsync(
                    client, clientHandler, requestToken, antiCookie, verifiedSessionCookie);
                await VerifyMembershipControlsAsync(
                    client, clientHandler, requestToken, antiCookie, verifiedSessionCookie);
                await VerifyTreeExceptionIsPrivateAsync(verifiedSessionCookie);

                using var invalidCsrf = new HttpRequestMessage(HttpMethod.Post, "https://localhost/private")
                {
                    Content = new FormUrlEncodedContent(scope)
                };
                invalidCsrf.Headers.TryAddWithoutValidation("Cookie",
                    verifiedSessionCookie + "; " + antiCookie);
                using var rejected = await client.SendAsync(invalidCsrf);
                Ensure(rejected.StatusCode == HttpStatusCode.BadRequest,
                    "private selection POST rejects missing antiforgery tokens");

                clientHandler.Authenticated = false;
                var revokedSessionForm = new Dictionary<string, string>(treePost);
                var requestsBeforeRevokedSession = clientHandler.RequestUris.Count;
                using var revokedSession = await PostAsync(client, revokedSessionForm,
                    requestToken, antiCookie, verifiedSessionCookie);
                var revokedSessionHtml = await revokedSession.Content.ReadAsStringAsync();
                Ensure(revokedSession.StatusCode == HttpStatusCode.OK &&
                    revokedSessionHtml.Contains("Sign in with Google", StringComparison.Ordinal) &&
                    !revokedSessionHtml.Contains("Fictional Center", StringComparison.Ordinal) &&
                    !revokedSessionHtml.Contains(LocalApiHandler.CenterId.ToString("D"),
                        StringComparison.Ordinal) &&
                    clientHandler.RequestUris.Count == requestsBeforeRevokedSession + 2,
                    "a revoked session returns to sign-in without reaching tree reads or showing previously authorized data");
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        private static async Task VerifyMembershipControlsAsync(
            HttpClient client,
            LocalApiHandler handler,
            string requestToken,
            string antiCookie,
            string sessionCookie)
        {
            var principalId = Guid.NewGuid();
            var invitationId = Guid.NewGuid();
            var memberId = Guid.NewGuid();
            var operations = new[]
            {
                (Action: "invite", Target: principalId,
                    Request: $"POST /api/v1/families/{LocalApiHandler.FamilyId:D}/invitations"),
                (Action: "accept", Target: invitationId,
                    Request: $"POST /api/v1/families/{LocalApiHandler.FamilyId:D}/invitations/{invitationId:D}/accept"),
                (Action: "approve", Target: memberId,
                    Request: $"POST /api/v1/families/{LocalApiHandler.FamilyId:D}/members/{memberId:D}/approve"),
                (Action: "revoke", Target: memberId,
                    Request: $"DELETE /api/v1/families/{LocalApiHandler.FamilyId:D}/members/{memberId:D}")
            };
            var start = handler.MembershipCalls.Count;
            var missingConfirmation = MembershipForm("revoke", memberId);
            missingConfirmation.Remove("confirmRevoke");
            using (var unconfirmedRevoke = await PostAsync(client,
                missingConfirmation, requestToken, antiCookie, sessionCookie))
                Ensure(unconfirmedRevoke.StatusCode == HttpStatusCode.BadRequest &&
                    handler.MembershipCalls.Count == start,
                    "revocation requires an explicit confirmation before API mutation");

            foreach (var operation in operations)
            {
                using var response = await PostAsync(client,
                    MembershipForm(operation.Action, operation.Target),
                    requestToken, antiCookie, sessionCookie);
                var html = await response.Content.ReadAsStringAsync();
                Ensure(response.StatusCode == HttpStatusCode.OK &&
                    html.Contains("Membership action accepted.", StringComparison.Ordinal),
                    "supported membership action reports success only after API confirmation");
                if (operation.Action == "invite")
                    Ensure(html.Contains("19a21fa7-2302-40eb-8f5e-7472ef64d0d5",
                            StringComparison.Ordinal),
                        "issued invitation identifier is returned only after API authorization");
            }

            Ensure(handler.MembershipCalls.Skip(start).SequenceEqual(
                    operations.Select(operation => operation.Request)) &&
                handler.MembershipBodies.Skip(start).First() is { } inviteBody &&
                JsonDocument.Parse(inviteBody).RootElement.GetProperty("intendedPrincipalId")
                    .GetGuid() == principalId &&
                handler.MembershipBodies.Skip(start).Skip(1).All(body => body is null),
                "membership calls match the documented invitation, acceptance, approval, and revocation routes");

            handler.MembershipStatus = HttpStatusCode.NotFound;
            var denied = await MembershipPageAsync(client, "approve", memberId,
                requestToken, antiCookie, sessionCookie);
            var unknown = await MembershipPageAsync(client, "approve", Guid.NewGuid(),
                requestToken, antiCookie, sessionCookie);
            Ensure(NormalizeToken(denied) == NormalizeToken(unknown) &&
                denied.Contains("Membership request unavailable.", StringComparison.Ordinal) &&
                !denied.Contains(memberId.ToString("D"), StringComparison.Ordinal),
                "forbidden and unknown membership operations have identical non-enumerating responses");
            handler.MembershipStatus = HttpStatusCode.OK;

            handler.CapabilitiesResponse =
                """{"authorization":"not yet configured","publicFamilyRecords":false}""";
            var open = new Dictionary<string, string>
            {
                ["action"] = "open",
                ["familyId"] = LocalApiHandler.FamilyId.ToString("D"),
                ["branchId"] = LocalApiHandler.BranchId.ToString("D"),
                ["above"] = "4",
                ["below"] = "4"
            };
            using (var noCapabilities = await PostAsync(client, open,
                       requestToken, antiCookie, sessionCookie))
            {
                var html = await noCapabilities.Content.ReadAsStringAsync();
                Ensure(!html.Contains("Membership controls", StringComparison.Ordinal),
                    "membership UI is hidden when the trusted API advertises no supported actions");
            }
            var callsBeforeUnsupportedAction = handler.MembershipCalls.Count;
            var unsupported = await MembershipPageAsync(client, "approve", memberId,
                requestToken, antiCookie, sessionCookie);
            Ensure(unsupported.Contains("Membership request unavailable.",
                    StringComparison.Ordinal) &&
                handler.MembershipCalls.Count == callsBeforeUnsupportedAction,
                "unsupported membership POSTs fail closed before contacting mutation routes");
            handler.CapabilitiesResponse =
                """{"membershipActions":["invite","accept","approve","revoke"]}""";
        }

        private static Dictionary<string, string> MembershipForm(string action, Guid targetId)
        {
            var form = new Dictionary<string, string>
            {
                ["action"] = "membership",
                ["familyId"] = LocalApiHandler.FamilyId.ToString("D"),
                ["branchId"] = LocalApiHandler.BranchId.ToString("D"),
                ["above"] = "4",
                ["below"] = "4",
                ["membershipAction"] = action,
                ["targetId"] = targetId.ToString("D")
            };
            if (action == "revoke")
                form["confirmRevoke"] = "true";
            return form;
        }

        private static async Task<string> MembershipPageAsync(
            HttpClient client,
            string action,
            Guid targetId,
            string requestToken,
            string antiCookie,
            string sessionCookie)
        {
            using var response = await PostAsync(client, MembershipForm(action, targetId),
                requestToken, antiCookie, sessionCookie);
            return await response.Content.ReadAsStringAsync();
        }

        private static string NormalizeToken(string html) => Regex.Replace(html,
            "(?<=name='__RequestVerificationToken' value=')[^']+", "TOKEN");

        private static async Task VerifyIndistinguishableDenialsAsync(
            HttpClient client,
            LocalApiHandler handler,
            string requestToken,
            string antiCookie,
            string sessionCookie)
        {
            async Task<string> DeniedPageAsync(HttpClient client,
                Dictionary<string, string> scope, string token, string cookie, Guid centerId)
            {
                var request = new Dictionary<string, string>(scope)
                {
                    ["action"] = "tree",
                    ["centerId"] = centerId.ToString("D"),
                    ["above"] = "4",
                    ["below"] = "4"
                };
                using var response = await PostAsync(client, request, token, cookie, sessionCookie);
                Ensure(response.StatusCode == HttpStatusCode.OK &&
                    response.Headers.CacheControl?.NoStore == true,
                    "unknown and forbidden reads are non-cacheable generic responses");
                return await response.Content.ReadAsStringAsync();
            }

            var scope = new Dictionary<string, string>
            {
                ["action"] = "open",
                ["familyId"] = LocalApiHandler.FamilyId.ToString("D"),
                ["branchId"] = LocalApiHandler.BranchId.ToString("D"),
                ["above"] = "4",
                ["below"] = "4"
            };
            handler.TreeStatus = HttpStatusCode.NotFound;
            var forbidden = await DeniedPageAsync(client, scope, requestToken, antiCookie,
                LocalApiHandler.ParentId);
            var unknown = await DeniedPageAsync(client, scope, requestToken, antiCookie,
                LocalApiHandler.HiddenId);
            var revokedMember = await DeniedPageAsync(client, scope, requestToken, antiCookie,
                LocalApiHandler.ChildId);
            static string NormalizeToken(string html) => Regex.Replace(html,
                "(?<=name='__RequestVerificationToken' value=')[^']+", "TOKEN");
            Ensure(NormalizeToken(forbidden) == NormalizeToken(unknown) &&
                NormalizeToken(unknown) == NormalizeToken(revokedMember) &&
                !unknown.Contains(LocalApiHandler.HiddenId.ToString("D"), StringComparison.Ordinal) &&
                !unknown.Contains("Fictional Parent", StringComparison.Ordinal),
                "forbidden, unknown, and membership-revoked API results are identical and reveal no identifiers");
        }

        private static async Task VerifyTreeExceptionIsPrivateAsync(string verifiedSessionCookie)
        {
            var handler = new LocalApiHandler
            {
                Authenticated = true,
                ExpectedSessionCookie = verifiedSessionCookie,
                ThrowOnTree = true
            };
            using var apiHttpClient = new HttpClient(handler);
            var options = new PrivateTreeModeOptions(true, new Uri("https://localhost:5043/"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddAntiforgery(antiforgery =>
            {
                antiforgery.Cookie.Name = "FamilyTreeWebAntiforgery";
                antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            });
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(apiHttpClient);
            builder.Services.AddSingleton<PrivateTreeApiClient>();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(context);
            });
            PrivateTreeRoutes.Map(app);
            await app.StartAsync();
            try
            {
                using var client = app.GetTestClient();
                using var initial = await GetPrivateAsync(client, verifiedSessionCookie);
                var antiCookie = GetCookie(initial, "FamilyTreeWebAntiforgery");
                var requestToken = GetFormToken(await initial.Content.ReadAsStringAsync());
                var request = new Dictionary<string, string>
                {
                    ["action"] = "tree",
                    ["familyId"] = LocalApiHandler.FamilyId.ToString("D"),
                    ["branchId"] = LocalApiHandler.BranchId.ToString("D"),
                    ["centerId"] = LocalApiHandler.CenterId.ToString("D"),
                    ["above"] = "4",
                    ["below"] = "4"
                };
                using var response = await PostAsync(client, request, requestToken, antiCookie,
                    verifiedSessionCookie);
                var html = await response.Content.ReadAsStringAsync();
                Ensure(response.StatusCode == HttpStatusCode.OK &&
                    html.Contains("Private records are unavailable", StringComparison.Ordinal) &&
                    !html.Contains("PRIVATE_EXCEPTION_SENTINEL", StringComparison.Ordinal) &&
                    !html.Contains("Fictional Center", StringComparison.Ordinal),
                    "API exceptions fail closed without leaking exception messages or data");
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client,
            Dictionary<string, string> values, string token, string antiCookie,
            string sessionCookie)
        {
            var form = new Dictionary<string, string>(values)
            {
                ["__RequestVerificationToken"] = token
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/private")
            {
                Content = new FormUrlEncodedContent(form)
            };
            request.Headers.TryAddWithoutValidation("Cookie", sessionCookie + "; " + antiCookie);
            return await client.SendAsync(request);
        }

        private static async Task<HttpResponseMessage> GetPrivateAsync(
            HttpClient client, string sessionCookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/private");
            request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            return await client.SendAsync(request);
        }

        private static string GetCookie(HttpResponseMessage response, string name)
        {
            var header = response.Headers.GetValues("Set-Cookie")
                .First(value => value.StartsWith(name + "=", StringComparison.Ordinal));
            return header.Split(';', 2)[0];
        }

        private static string GetFormToken(string html)
        {
            const string prefix = "name='__RequestVerificationToken' value='";
            var start = html.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("Antiforgery form token not rendered.");
            start += prefix.Length;
            var end = html.IndexOf('\'', start);
            if (end < 0)
                throw new InvalidOperationException("Antiforgery form token is malformed.");
            return WebUtility.HtmlDecode(html[start..end]);
        }
    private static IConfiguration CreateUnsafeApiConfiguration(string value) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [PrivateTreeModeOptions.EnabledSetting] = "true",
                [PrivateTreeModeOptions.ApiBaseUrlSetting] = value
            }).Build();

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
