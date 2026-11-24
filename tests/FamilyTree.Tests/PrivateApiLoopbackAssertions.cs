using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FamilyTree.Api;
using FamilyTree.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class PrivateApiLoopbackAssertions
{
    [SupportedOSPlatform("linux")]
    internal static async Task RunAsync()
    {
        var directory = Path.Join(Path.GetTempPath(), "familytree-private-api-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var databasePath = Path.Combine(directory, "familytree.sqlite");
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var identityKey = RandomNumberGenerator.GetBytes(32);
        var certificatePassword = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var certificatePath = Path.Combine(directory, "data-protection.pfx");
        const string authority = "https://accounts.google.com";
        const string adminSubject = "synthetic-private-api-admin";
        const string memberSubject = "synthetic-private-api-member";
        var provider = new InteractiveOidc.Provider("google", "GoogleOidc",
            authority, "synthetic-client", "synthetic-secret", "/auth/callback/google");
        var familyId = Guid.NewGuid();
        Guid adminId;
        Guid memberId;

        try
        {
            CreateSyntheticCertificate(certificatePath, certificatePassword);
            using (var registry = new SqlitePrincipalRegistry(databasePath, identityKey))
            using (var familyStore = new SqliteFamilyStore(databasePath, encryptionKey))
            {
                adminId = registry.EnrollVerifiedSubject("google", authority, adminSubject);
                memberId = registry.EnrollVerifiedSubject("google", authority, memberSubject);
                familyStore.CreateFictionalFamily(familyId, adminId);
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FAMILYTREE_PRIVATE_API_ENABLED"] = "true",
                ["FAMILYTREE_PRIVATE_API_DATA_DIRECTORY"] = directory,
                ["FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY"] =
                    Convert.ToBase64String(encryptionKey),
                ["FAMILYTREE_PRIVATE_API_IDENTITY_KEY"] =
                    Convert.ToBase64String(identityKey),
                ["FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE"] = certificatePath,
                ["FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD"] =
                    certificatePassword,
                ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"] = "synthetic-client",
                ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"] = "synthetic-secret"
            });
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0,
                    listen => listen.UseHttps(certificatePath, certificatePassword)));
            await using var app = ApiApplication.Build(builder);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            Check(addresses is { Count: > 0 }, "private API binds loopback");
            var baseAddress = new Uri(addresses!.Single(
                address => address.StartsWith("https://127.0.0.1:", StringComparison.Ordinal)));
            using var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            }) { BaseAddress = baseAddress };

            var adminCookie = await CreateCookieAsync(app, provider, adminSubject);
            var memberCookie = await CreateCookieAsync(app, provider, memberSubject);

            using (var invalidHost = new HttpRequestMessage(HttpMethod.Get, "/api/v1/csrf"))
            {
                invalidHost.Headers.Host = "outside.example";
                invalidHost.Headers.TryAddWithoutValidation("Cookie", adminCookie);
                using var response = await client.SendAsync(invalidHost);
                Check(response.StatusCode == HttpStatusCode.NotImplemented &&
                    response.Headers.CacheControl?.NoStore == true,
                    "host header cannot expose local private routes");
            }

            using (var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/csrf"))
            {
                forged.Headers.TryAddWithoutValidation("Cookie",
                    "__Host-FamilyTreeSession=forged");
                using var response = await client.SendAsync(forged);
                Check(response.StatusCode == HttpStatusCode.NotFound &&
                    response.Content.Headers.ContentLength == 0,
                    "forged signed cookie denied with empty response");
            }

            var adminCsrf = await GetCsrfAsync(client, adminCookie);
            using (var registration = await SendWriteAsync(client, HttpMethod.Post,
                       "/api/v1/me", adminCsrf, adminCookie))
            {
                var json = await registration.Content.ReadFromJsonAsync<JsonElement>();
                Check(registration.StatusCode == HttpStatusCode.OK &&
                    json.GetProperty("principalId").GetGuid() == adminId,
                    "verified cookie maps to stable registered principal");
            }

            var memberCsrf = await GetCsrfAsync(client, memberCookie);
            using (var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me"))
            {
                missingCsrf.Headers.TryAddWithoutValidation("Cookie", adminCookie);
                missingCsrf.Headers.TryAddWithoutValidation("Origin", baseAddress.GetLeftPart(UriPartial.Authority));
                using var response = await client.SendAsync(missingCsrf);
                Check(response.StatusCode == HttpStatusCode.NotFound &&
                    response.Content.Headers.ContentLength == 0,
                    "cookie-authenticated writes require anti-forgery token");
            }

            var invitationPath = $"/api/v1/families/{familyId:D}/invitations";
            using var invitationResponse = await SendWriteAsync(client, HttpMethod.Post,
                invitationPath, adminCsrf, adminCookie,
                JsonContent.Create(new { intendedPrincipalId = memberId }));
            var invitationJson = await invitationResponse.Content.ReadFromJsonAsync<JsonElement>();
            var invitationId = invitationJson.GetProperty("invitationId").GetGuid();
            Check(invitationResponse.StatusCode == HttpStatusCode.OK &&
                invitationId != Guid.Empty, "active administrator can issue an invitation");

            using (var wrongOrigin = new HttpRequestMessage(HttpMethod.Post, invitationPath))
            {
                wrongOrigin.Headers.TryAddWithoutValidation("Cookie",
                    adminCsrf.CookieHeader(adminCookie));
                wrongOrigin.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", adminCsrf.RequestToken);
                wrongOrigin.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
                wrongOrigin.Content = JsonContent.Create(new { intendedPrincipalId = memberId });
                using var response = await client.SendAsync(wrongOrigin);
                Check(response.StatusCode == HttpStatusCode.NotFound &&
                    response.Content.Headers.ContentLength == 0,
                    "cross-origin cookie-authenticated writes are denied");
            }

            var acceptPath = $"{invitationPath}/{invitationId:D}/accept";
            using (var accepted = await SendWriteAsync(client, HttpMethod.Post,
                       acceptPath, memberCsrf, memberCookie))
                Check(accepted.StatusCode == HttpStatusCode.NoContent,
                    "intended principal can accept an invitation");
            Check(!app.Services.GetRequiredService<SqliteFamilyStore>()
                .IsActiveMember(familyId, memberId), "accepted member remains pending");

            var approvePath = $"/api/v1/families/{familyId:D}/members/{memberId:D}/approve";
            using (var approved = await SendWriteAsync(client, HttpMethod.Post,
                       approvePath, adminCsrf, adminCookie))
                Check(approved.StatusCode == HttpStatusCode.NoContent,
                    "active administrator can approve a pending member");
            Check(app.Services.GetRequiredService<SqliteFamilyStore>()
                .IsActiveMember(familyId, memberId), "approval activates membership");

            var revokePath = $"/api/v1/families/{familyId:D}/members/{memberId:D}";
            using (var revoked = await SendWriteAsync(client, HttpMethod.Delete,
                       revokePath, adminCsrf, adminCookie))
                Check(revoked.StatusCode == HttpStatusCode.NoContent,
                    "active administrator can revoke a member");
            Check(!app.Services.GetRequiredService<SqliteFamilyStore>()
                .IsActiveMember(familyId, memberId), "revocation takes effect immediately");

            var denied = await SendWriteAsync(client, HttpMethod.Post,
                $"/api/v1/families/{Guid.NewGuid():D}/invitations",
                adminCsrf, adminCookie,
                JsonContent.Create(new { intendedPrincipalId = memberId }));
            using (denied)
            using (var unknown = await SendWriteAsync(client, HttpMethod.Post,
                       $"/api/v1/families/{familyId:D}/invitations",
                       memberCsrf, memberCookie,
                       JsonContent.Create(new { intendedPrincipalId = adminId })))
            {
                Check(denied.StatusCode == HttpStatusCode.NotFound &&
                    unknown.StatusCode == denied.StatusCode &&
                    await denied.Content.ReadAsStringAsync() ==
                    await unknown.Content.ReadAsStringAsync(),
                    "forbidden and unknown family requests are indistinguishable");
            }

            using (var stillClosed = await client.GetAsync("/api/v1/people"))
                Check(stillClosed.StatusCode == HttpStatusCode.NotImplemented &&
                    stillClosed.Headers.CacheControl?.NoStore == true,
                    "private reads remain closed until the reviewed grant contract is integrated");

            await app.StopAsync();
            Console.WriteLine("PASS loopback private API signed-cookie, CSRF and membership lifecycle");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(identityKey);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> CreateCookieAsync(WebApplication app,
        InteractiveOidc.Provider provider, string subject)
    {
        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("127.0.0.1");
        context.Response.Body = new MemoryStream();
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        VerifiedOidcSession.Stamp(identity, provider, provider.Authority, subject);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            });
        return string.Join("; ", context.Response.Headers.SetCookie
            .Select(value => value!.Split(';')[0]));
    }

    [SupportedOSPlatform("linux")]
    private static void CreateSyntheticCertificate(string path, string password)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=FamilyTree synthetic test",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false,
            pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        var enhancedKeyUsages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            enhancedKeyUsages, critical: false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(path,
            certificate.Export(X509ContentType.Pfx, password));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<CsrfContext> GetCsrfAsync(HttpClient client, string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/csrf");
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.OK, "verified cookie can retrieve CSRF token");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var csrfCookie = response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';')[0])
            .Single(value => value.StartsWith("__Host-FamilyTreeAntiforgery=", StringComparison.Ordinal));
        return new CsrfContext(body.GetProperty("requestToken").GetString()!, csrfCookie);
    }

    private static async Task<HttpResponseMessage> SendWriteAsync(HttpClient client,
        HttpMethod method, string path, CsrfContext csrf, string sessionCookie,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Cookie", csrf.CookieHeader(sessionCookie));
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf.RequestToken);
        request.Headers.TryAddWithoutValidation("Origin",
            client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }

    private sealed record CsrfContext(string RequestToken, string Cookie)
    {
        internal string CookieHeader(string sessionCookie) => $"{sessionCookie}; {Cookie}";
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
