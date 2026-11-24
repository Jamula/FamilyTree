using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var repositoryRoot = FindRepositoryRoot();
var apiProject = Path.Combine(repositoryRoot, "src", "FamilyTree.Api", "FamilyTree.Api.csproj");
var port = GetAvailablePort();
var apiAddress = new Uri($"http://127.0.0.1:{port}");
var isolatedHome = Directory.CreateTempSubdirectory("familytree-acceptance-");
var startInfo = new ProcessStartInfo("dotnet")
{
    WorkingDirectory = repositoryRoot,
    UseShellExecute = false
};
startInfo.ArgumentList.Add("run");
startInfo.ArgumentList.Add("--project");
startInfo.ArgumentList.Add(apiProject);
startInfo.ArgumentList.Add("--configuration");
startInfo.ArgumentList.Add("Release");
startInfo.ArgumentList.Add("--no-build");
startInfo.ArgumentList.Add("--urls");
startInfo.ArgumentList.Add(apiAddress.ToString());
startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
startInfo.Environment["HOME"] = isolatedHome.FullName;
startInfo.Environment["USERPROFILE"] = isolatedHome.FullName;
startInfo.Environment["APPDATA"] = Path.Combine(isolatedHome.FullName, "AppData", "Roaming");
startInfo.Environment["LOCALAPPDATA"] = Path.Combine(isolatedHome.FullName, "AppData", "Local");
startInfo.Environment.Remove("ASPNETCORE_HTTP_PORTS");
startInfo.Environment.Remove("ASPNETCORE_HTTPS_PORTS");
foreach (var key in new[]
{
    "FAMILYTREE_OIDC_GOOGLE_CLIENT_ID",
    "FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET",
    "FAMILYTREE_OIDC_MICROSOFT_TENANT_ID",
    "FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID",
    "FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"
})
    startInfo.Environment.Remove(key);

Process? apiProcess = null;
try
{
    apiProcess = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the local API process.");
    using var handler = new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    };
    using var client = new HttpClient(handler)
    {
        BaseAddress = apiAddress,
        Timeout = TimeSpan.FromSeconds(5)
    };

    await WaitForApiAsync(client, apiProcess);
    var health = await GetAsync(client, "/health");
    Require(health.StatusCode == HttpStatusCode.OK &&
            health.Body.Contains("\"status\":\"ok\"", StringComparison.Ordinal),
        "real API process health response");
    Console.WriteLine("PASS real local API process and HTTP health startup");

    var capabilities = await GetAsync(client, "/api/v1/capabilities");
    Require(capabilities.StatusCode == HttpStatusCode.OK &&
            capabilities.Body.Contains("\"publicFamilyRecords\":false", StringComparison.Ordinal),
        "public capabilities keep family records private");
    var providers = await GetAsync(client, "/auth/providers");
    Require(providers.StatusCode == HttpStatusCode.OK &&
            providers.Body.Contains("\"providers\":[]", StringComparison.Ordinal) &&
            providers.Body.Contains("\"membershipAuthorization\":false", StringComparison.Ordinal),
        "unconfigured OIDC providers remain disabled");
    Require((await GetAsync(client, "/auth/login/google")).StatusCode == HttpStatusCode.NotFound,
        "unconfigured OIDC login is unavailable");
    Console.WriteLine("PASS default-off OIDC configuration and explicit public privacy flag");

    var sessionResponses = new List<(string Name, HttpStatusCode Status)>();
    foreach (var cookie in new string?[]
    {
        null,
        "__Host-FamilyTreeSession=forged.invalid.signature",
        "__Host-FamilyTreeSession=not-a-protected-ticket"
    })
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request);
        sessionResponses.Add((cookie ?? "anonymous", response.StatusCode));
    }
    Require(sessionResponses.All(result => result.Status == HttpStatusCode.Unauthorized),
        "anonymous and forged session cookies must not authenticate");
    Console.WriteLine("PASS anonymous and forged-cookie session denials");

    var privatePaths = new[]
    {
        "/api/v1/people/synthetic-private-record",
        "/api/v1/people/unknown-record",
        "/api/v1/tree?person=synthetic-private-record",
        "/api/v1/search?q=Fictional",
        "/api/v1/families/synthetic-family",
        "/api/v1/exports/gedcom"
    };
    var privateResponses = new List<PrivateResponse>();
    foreach (var path in privatePaths)
    {
        foreach (var identity in new[] { "none", "forged-cookie", "spoofed-headers" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (identity == "forged-cookie")
                request.Headers.TryAddWithoutValidation("Cookie",
                    "__Host-FamilyTreeSession=forged.invalid.signature");
            if (identity == "spoofed-headers")
            {
                request.Headers.TryAddWithoutValidation("Authorization", "******");
                request.Headers.TryAddWithoutValidation("X-Forwarded-User", "fictional-administrator");
                request.Headers.TryAddWithoutValidation("X-Family-ID", "synthetic-family");
            }

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var hasNoStore = response.Headers.CacheControl?.NoStore == true;
            var hasNoSniff = response.Headers.TryGetValues("X-Content-Type-Options",
                out var values) && values.Contains("nosniff", StringComparer.Ordinal);
            Require(response.StatusCode == HttpStatusCode.NotImplemented &&
                    body.Length == 0 &&
                    response.Content.Headers.ContentLength == 0 &&
                    hasNoStore && hasNoSniff,
                $"reserved private path remains empty and fail-closed: {path} ({identity})");
            privateResponses.Add(new(response.StatusCode, body, response.Content.Headers.ContentLength,
                hasNoStore, hasNoSniff));
        }
    }
    Require(privateResponses.Distinct().Count() == 1,
        "reserved-path and caller-identity response equivalence");
    using (var write = new HttpRequestMessage(HttpMethod.Post, "/api/v1/people")
    {
        Content = new StringContent("{\"name\":\"Synthetic Acceptance Person\"}")
    })
    using (var response = await client.SendAsync(write))
    {
        Require(response.StatusCode == HttpStatusCode.NotImplemented &&
                (await response.Content.ReadAsStringAsync()).Length == 0,
            "private writes remain closed without echoing synthetic input");
    }
    Console.WriteLine("PASS reserved-route 501 baseline, empty-body equivalence, forged identity denial and write denial");

    Console.WriteLine("UNACCEPTED: valid expired/revoked-cookie rejection; no trusted OIDC login or approved session fixture exists.");
    Console.WriteLine("UNACCEPTED: forbidden-versus-unknown record distinction; private records and authorized handlers are not wired.");
    Console.WriteLine("UNACCEPTED: membership, CRUD/tree, editing, GEDCOM, restart/backup, Windows UI and complete privacy matrix.");
    Console.WriteLine("This process-level baseline is not evidence that the private-family MVP is complete.");
}
finally
{
    if (apiProcess is not null)
    {
        if (!apiProcess.HasExited)
            apiProcess.Kill(entireProcessTree: true);
        await apiProcess.WaitForExitAsync();
        apiProcess.Dispose();
    }
    Directory.Delete(isolatedHome.FullName, recursive: true);
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
         directory is not null;
         directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "FamilyTree.slnx")))
            return directory.FullName;
    }

    throw new DirectoryNotFoundException("Run the acceptance harness from inside the repository.");
}

static int GetAvailablePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static async Task WaitForApiAsync(HttpClient client, Process process)
{
    var deadline = DateTime.UtcNow.AddSeconds(25);
    while (DateTime.UtcNow < deadline)
    {
        if (process.HasExited)
            throw new InvalidOperationException($"Local API exited during startup (code {process.ExitCode}).");

        try
        {
            using var response = await client.GetAsync("/health");
            if (response.StatusCode == HttpStatusCode.OK)
                return;
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!process.HasExited)
        {
        }

        await Task.Delay(250);
    }

    throw new TimeoutException("Local API did not become ready within 25 seconds.");
}

static async Task<(HttpStatusCode StatusCode, string Body)> GetAsync(HttpClient client, string path)
{
    using var response = await client.GetAsync(path);
    return (response.StatusCode, await response.Content.ReadAsStringAsync());
}

static void Require(bool condition, string evidence)
{
    if (!condition)
        throw new InvalidOperationException($"FAIL {evidence}");
}

sealed record PrivateResponse(HttpStatusCode Status, string Body, long? ContentLength,
    bool NoStore, bool NoSniff);
