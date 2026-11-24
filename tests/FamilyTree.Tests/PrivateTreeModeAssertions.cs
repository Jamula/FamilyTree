using System.Net;
using System.Text;
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri?.AbsolutePath == "/auth/providers")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"providers":["google","microsoft","unknown"]}"""));
            if (request.RequestUri?.AbsolutePath == "/auth/session")
            {
                SessionCookie = request.Headers.TryGetValues("Cookie", out var values)
                    ? values.Single()
                    : null;
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, ""));
            }
            throw new InvalidOperationException("Unexpected API route.");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
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

    public static async Task RunAsync()
    {
        var defaults = PrivateTreeModeOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Ensure(!defaults.Enabled && defaults.ApiBaseUri is null, "private mode is disabled by default");

        var invalidValues = new[]
        {
            "http://localhost:5043",
            "https://api.example.test",
            "https://user@localhost:5043",
            "https://localhost:5043/path"
        };
        foreach (var value in invalidValues)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [PrivateTreeModeOptions.EnabledSetting] = "true",
                    [PrivateTreeModeOptions.ApiBaseUrlSetting] = value
                }).Build();
            try
            {
                PrivateTreeModeOptions.FromConfiguration(configuration);
                throw new InvalidOperationException("Unsafe API base URL was accepted.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("FAMILYTREE_API_BASE_URL", StringComparison.Ordinal))
            {
            }
        }

        await VerifyPrivateRouteAsync();
        VerifyDepthLimits();
        Console.WriteLine("PASS private web mode and deep synthetic traversal assertions");
    }

    private static async Task VerifyPrivateRouteAsync()
    {
        var handler = new LocalApiHandler();
        var options = new PrivateTreeModeOptions(true, new Uri("https://localhost:5043/"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new HttpClient(handler));
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
                handler.SessionCookie == "__Host-FamilyTreeSession=not-a-real-ticket" &&
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
        disabledBuilder.Services.AddSingleton(new PrivateTreeModeOptions(false, null));
        disabledBuilder.Services.AddSingleton(new HttpClient(new LocalApiHandler()));
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
        Ensure(depthOne?.Nodes.Count == 3 &&
            depthOne.Nodes.Min(x => x.Generation) == -1 &&
            depthOne.Nodes.Max(x => x.Generation) == 1,
            "depth one includes one ancestor and descendant");

        var depthEight = PersonTreeProjector.ProjectAsync(Guid.NewGuid(), center.Id,
            people, relationships, new TreeOptions(8, 8, false, false, false),
            authorization).GetAwaiter().GetResult();
        Ensure(depthEight?.Nodes.Count == 17 &&
            depthEight.Nodes.Min(x => x.Generation) == -8 &&
            depthEight.Nodes.Max(x => x.Generation) == 8,
            "depth eight traverses the complete synthetic graph without truncation");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
