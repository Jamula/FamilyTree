using System.Net;
using FamilyTree.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class ApiLoopbackIntegrationAssertions
{
    internal static async Task RunAsync()
    {
        Expect<ArgumentNullException>(() => ApiApplication.Build(null!),
            "null API startup builder rejected");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        // Prevent developer/CI secrets or machine environment from configuring
        // identity providers in this synthetic integration test.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");

        await using var app = ApiApplication.Build(builder);
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        Check(addresses is not null && addresses.Count > 0,
            "Kestrel must expose a bound local port");
        var baseAddress = new Uri(addresses!.Single(x =>
            x.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };

        using (var health = await client.GetAsync("/health"))
        {
            Check(health.StatusCode == HttpStatusCode.OK &&
                (await health.Content.ReadAsStringAsync()).Contains("\"status\":\"ok\"",
                    StringComparison.Ordinal), "public health endpoint");
        }
        using (var capabilities = await client.GetAsync("/api/v1/capabilities"))
        {
            var content = await capabilities.Content.ReadAsStringAsync();
            Check(capabilities.StatusCode == HttpStatusCode.OK &&
                content.Contains("\"publicFamilyRecords\":false", StringComparison.Ordinal),
                "public capabilities never assert open family records");
        }
        using (var providers = await client.GetAsync("/auth/providers"))
        {
            var content = await providers.Content.ReadAsStringAsync();
            Check(providers.StatusCode == HttpStatusCode.OK &&
                content.Contains("\"providers\":[]", StringComparison.Ordinal) &&
                content.Contains("\"membershipAuthorization\":false", StringComparison.Ordinal),
                "default disabled identity providers");
        }
        using (var session = await client.GetAsync("/auth/session"))
            Check(session.StatusCode == HttpStatusCode.Unauthorized,
                "anonymous cookie session denied");
        using (var noProvider = await client.GetAsync("/auth/login/google"))
            Check(noProvider.StatusCode == HttpStatusCode.NotFound,
                "unconfigured login provider denied");
        using (var error = await client.GetAsync("/auth/error"))
            Check(error.StatusCode == HttpStatusCode.Unauthorized,
                "login error handled as an HTTP denial");

        foreach (var path in new[]
        {
            "/api/v1/people", "/api/v1/people/a", "/api/v1/people/b",
            "/api/v1/tree", "/api/v1/families/123", "/api/v1/profile-claims",
            "/api/v1/invitations", "/api/v1/search", "/api/v1/media/1",
            "/api/v1/exports", "/api/v1/imports", "/api/v1/ai",
            "/api/v1/mcp", "/api/v1/admin/members"
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer forged");
            request.Headers.TryAddWithoutValidation("X-Forwarded-User", "administrator");
            request.Headers.TryAddWithoutValidation("X-Family-ID", "spoof");
            using var result = await client.SendAsync(request);
            Check(result.StatusCode == HttpStatusCode.NotImplemented &&
                await result.Content.ReadAsStringAsync() == "" &&
                result.Headers.CacheControl?.NoStore == true,
                $"reserved private path must fail identically: {path}");
        }

        using (var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/people")
            { Content = new StringContent("{\"name\":\"fictional\"}") })
        using (var response = await client.SendAsync(post))
            Check(response.StatusCode == HttpStatusCode.NotImplemented &&
                await response.Content.ReadAsStringAsync() == "",
                "private write remains closed for all verbs");

        // Complete reserved-route verb/security matrix. A forged Cookie or
        // "administrator" header must not cause even a different response
        // shape until the real membership/field permissions are integrated.
        var privatePaths = new[]
        {
            "/api/v1/people/hidden-person", "/api/v1/people/unknown-person",
            "/api/v1/tree/hidden", "/api/v1/families",
            "/api/v1/families/fake", "/api/v1/families/00000000-0000-0000-0000-000000000001/branches",
            "/api/v1/families/00000000-0000-0000-0000-000000000001/members",
            "/api/v1/memberships/active", "/api/v1/me", "/api/v1/admin",
            "/api/v1/invitations", "/api/v1/profile-claims",
            "/api/v1/search?name=Fictional", "/api/v1/relationships/hidden",
            "/api/v1/media/private", "/api/v1/stories/private",
            "/api/v1/gratitude", "/api/v1/exports/gedcom",
            "/api/v1/imports", "/api/v1/ai", "/api/v1/mcp",
            "/api/v1/private/unknown"
        };
        var methods = new[] {
            HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch,
            HttpMethod.Delete, HttpMethod.Options, HttpMethod.Head
        };
        foreach (var privatePath in privatePaths)
        {
            foreach (var method in methods)
            {
                using var attempt = new HttpRequestMessage(method, privatePath);
                attempt.Headers.TryAddWithoutValidation("Authorization",
                    "Bearer forged.signed.token");
                attempt.Headers.TryAddWithoutValidation("Cookie",
                    "__Host-FamilyTreeSession=forged-cookie");
                attempt.Headers.TryAddWithoutValidation("X-Forwarded-User",
                    "fictional-administrator");
                attempt.Headers.TryAddWithoutValidation("X-Family-ID", Guid.NewGuid().ToString());
                if (method is not null && (method == HttpMethod.Post ||
                    method == HttpMethod.Put || method == HttpMethod.Patch))
                    attempt.Content = new StringContent("{\"person\":\"Fictional\"}");
                using var blocked = await client.SendAsync(attempt);
                Check(blocked.StatusCode == HttpStatusCode.NotImplemented &&
                    await blocked.Content.ReadAsStringAsync() == "" &&
                    blocked.Content.Headers.ContentLength == 0 &&
                    blocked.Headers.CacheControl?.NoStore == true &&
                    blocked.Headers.TryGetValues("X-Content-Type-Options",
                        out var contentTypeOptions) &&
                    contentTypeOptions.Contains("nosniff", StringComparer.Ordinal),
                    $"private {method} {privatePath} must fail closed with no response body");
            }
        }
        // Prefix matching must respect complete path segments, otherwise
        // a new public endpoint with a similar prefix is unintentionally masked.
        foreach (var publicUnknown in new[]
        {
            "/api/v1/peopleExtra", "/api/v1/treehouse",
            "/api/v1/familiesPublic"
        })
        {
            using var response = await client.GetAsync(publicUnknown);
            Check(response.StatusCode == HttpStatusCode.NotFound,
                $"path segment comparison must not block unrelated route: {publicUnknown}");
        }
        Console.WriteLine("PASS HTTP private route matrix: 133 verb/path pairs and malicious cookie/headers");

        using (var unknown = await client.GetAsync("/nothing-public-here"))
            Check(unknown.StatusCode == HttpStatusCode.NotFound,
                "unknown public routes remain not found");

        await app.StopAsync();
        Console.WriteLine("PASS in-process API health, OIDC errors and private route enumeration tests");
    }

    private static void Check(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }

    private static void Expect<T>(Action operation, string why) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException(why);
    }
}
