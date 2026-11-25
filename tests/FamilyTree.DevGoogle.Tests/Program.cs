using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using FamilyTree.DevGoogle;

if (args.Length > 0 && args[0] == "--child-check")
{
    var okay = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null &&
        Environment.GetEnvironmentVariable("DOTNET_Kestrel__Endpoints__Evil__Url") is null &&
        Environment.GetEnvironmentVariable("FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET") is null &&
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is null &&
        Environment.GetEnvironmentVariable("FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET") == "fictional-secret" &&
        args.Skip(1).SequenceEqual(new[] { "--urls", "https://localhost:5182", "--contentRoot", Environment.CurrentDirectory });
    return okay ? 0 : 1;
}

var credentials = new Credentials("fictional-client-id", "fictional-secret");
Check(!credentials.ToString()!.Contains("fictional"), "credentials do not stringify");
foreach (var value in new[] { "", " ", " padded ", "line\nbreak", new string('x', 1001) })
{
    ExpectFailure(() => new Credentials(value, "valid"), "invalid client id");
    ExpectFailure(() => new Credentials("valid", value), "invalid secret");
}

var poison = new Dictionary<string, string>
{
    ["ASPNETCORE_URLS"] = "http://0.0.0.0:1234",
    ["DOTNET_Kestrel__Endpoints__Evil__Url"] = "http://0.0.0.0:1234",
    ["ASPNETCORE_FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"] = "fictional-other",
    ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"] = "fictional-other-secret",
    ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://example.invalid",
    ["ASPNETCORE_CONTENTROOT"] = "hostile",
    ["DOTNET_ENVIRONMENT"] = "Development",
    ["Kestrel__Endpoints__Evil__Url"] = "http://0.0.0.0:1234",
    ["Logging__LogLevel__Default"] = "Trace",
    ["EXTRA_CONFIG"] = "hostile"
};
foreach (var pair in poison) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
var root = Environment.CurrentDirectory;
var start = ApiLauncher.CreateStartInfo("dotnet", "api.dll", root, "windows", "profile", "roaming", "local", credentials);
Check(start.ArgumentList.SequenceEqual(new[] { "api.dll", "--urls", "https://localhost:5182", "--contentRoot", root }), "fixed launch arguments");
Check(!start.ArgumentList.Any(x => x.Contains("fictional")), "arguments contain no credentials");
Check(start.WorkingDirectory == root && start.RedirectStandardOutput && start.RedirectStandardError && !start.UseShellExecute, "isolated process and output");
foreach (var pair in poison.Where(p => p.Key is not ("DOTNET_ENVIRONMENT" or "Logging__LogLevel__Default")))
    Check(!start.Environment.ContainsKey(pair.Key), "hostile configuration removed");
Check(start.Environment["DOTNET_ENVIRONMENT"] == "Production", "production host environment");
Check(start.Environment["Logging__LogLevel__Default"] == "None", "logging disabled");
Check(start.Environment["AllowedHosts"] == "localhost", "host filtering");
Check(start.Environment["FAMILYTREE_OTLP_ENABLED"] == "false", "telemetry disabled");

var dotnet = Environment.ProcessPath ?? throw new Exception("No runtime executable.");
if (Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
{
    var assembly = typeof(Credentials).Assembly.Location;
    var cli = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
    cli.ArgumentList.Add(assembly);
    cli.ArgumentList.Add("fictional-argument-secret");
    using var p = Process.Start(cli) ?? throw new Exception("Could not start launcher.");
    var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    Check(p.ExitCode == 2 && !output.Contains("fictional-argument-secret"), "invalid arguments are never echoed");

    if (!OperatingSystem.IsWindows())
    {
        var unsupported = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
        unsupported.ArgumentList.Add(assembly);
        unsupported.ArgumentList.Add("start");
        using var p2 = Process.Start(unsupported) ?? throw new Exception("Could not start launcher.");
        var result = await p2.StandardOutput.ReadToEndAsync() + await p2.StandardError.ReadToEndAsync();
        await p2.WaitForExitAsync();
        Check(p2.ExitCode == 1 && result.Contains("Windows") && !result.Contains("fictional"), "non-Windows fails closed");
    }

    var testAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
    var child = ApiLauncher.CreateStartInfo(dotnet, testAssembly, root,
        Environment.GetEnvironmentVariable("SystemRoot") ?? "", root, root, root, credentials);
    child.ArgumentList.Insert(1, "--child-check");
    using var p3 = Process.Start(child) ?? throw new Exception("Could not start test child.");
    await p3.WaitForExitAsync();
    Check(p3.ExitCode == 0, "child receives only intended settings");
}
else
    throw new Exception("Run tests via dotnet, not an apphost.");

if (args.Length == 2 && args[0] == "--integration-api")
    await TestApi(args[1], credentials);
if (OperatingSystem.IsWindows()) TestWindowsStore();
else Console.WriteLine("Windows Credential Manager lifecycle tests skipped on this platform.");
Console.WriteLine("Launcher tests passed.");
return 0;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void ExpectFailure(Action action, string message)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception(message);
}

static async Task TestApi(string api, Credentials credentials)
{
    var parent = Path.Combine(AppContext.BaseDirectory, "isolation-" + Guid.NewGuid().ToString("N"));
    var content = Path.Combine(parent, "empty");
    var hostile = Path.Combine(parent, "hostile");
    Directory.CreateDirectory(content);
    Directory.CreateDirectory(hostile);
    await File.WriteAllTextAsync(Path.Combine(hostile, "appsettings.json"),
        """{"Kestrel":{"Endpoints":{"Evil":{"Url":"http://0.0.0.0:5183"}}},"FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID":"bad"}""");
    Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", hostile);
    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var start = ApiLauncher.CreateStartInfo(Environment.ProcessPath!, Path.GetFullPath(api), content,
        Environment.GetEnvironmentVariable("SystemRoot") ?? "", profile,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), credentials);
    if (!OperatingSystem.IsWindows()) start.Environment["HOME"] = profile;
    try
    {
        using var process = Process.Start(start) ?? throw new Exception("Could not start API.");
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            HttpResponseMessage? response = null;
            for (var i = 0; i < 50 && response is null && !process.HasExited; i++)
            {
                try { response = await client.GetAsync("https://localhost:5182/auth/providers"); }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Check(response is not null && response.StatusCode == HttpStatusCode.OK, "API started on localhost HTTPS");
            using (response)
            {
                var body = await response!.Content.ReadAsStringAsync();
                Check(body.Contains("google") && !body.Contains("microsoft") && !body.Contains("fictional"), "only Google is configured without credential disclosure");
            }
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(x => x.Port == 5182).ToArray();
            Check(listeners.Length > 0 && listeners.All(x => IPAddress.IsLoopback(x.Address)), "listeners restricted to loopback");
            Check(!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 5183), "hostile content root not loaded");
            using var badHost = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5182/health");
            badHost.Headers.Host = "example.invalid";
            using var denied = await client.SendAsync(badHost);
            Check(denied.StatusCode == HttpStatusCode.BadRequest, "foreign host denied");
            using var microsoft = await client.GetAsync("https://localhost:5182/auth/login/microsoft");
            Check(microsoft.StatusCode == HttpStatusCode.NotFound, "Microsoft is not enabled");
            using var records = await client.GetAsync("https://localhost:5182/api/v1/people");
            Check(records.StatusCode == HttpStatusCode.NotImplemented, "private records remain unavailable");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }
    finally { Directory.Delete(parent, recursive: true); }
}

[SupportedOSPlatform("windows")]
static void TestWindowsStore()
{
    var store = new WindowsCredentialStore("FamilyTree.DevGoogle.Tests." + Guid.NewGuid().ToString("N"));
    try
    {
        Check(store.Read() is null, "store initially empty");
        store.Remove();
        var first = new Credentials("fictional-id-one", "fictional-secret-one");
        store.Write(first);
        var read = store.Read();
        Check(read?.ClientId == first.ClientId && read.ClientSecret == first.ClientSecret, "store round trip");
        var replacement = new Credentials("fictional-id-two", "fictional-secret-two");
        store.Write(replacement);
        read = store.Read();
        Check(read?.ClientId == replacement.ClientId && read.ClientSecret == replacement.ClientSecret, "store update");
        store.Remove();
        store.Remove();
        Check(store.Read() is null, "store removal idempotent");
    }
    finally { store.Remove(); }
}
