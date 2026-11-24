using System.Net;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using FamilyTree.Api;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;
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
    internal static void AssertWindowsConfiguration()
    {
        Check(PrivateApiComposition.EnvironmentBlocker(
                isDevelopment: true, isLinux: false, isWindows: true).Length == 0 &&
            PrivateApiComposition.EnvironmentBlocker(
                isDevelopment: false, isLinux: false, isWindows: true).Length > 0,
            "Windows private API is limited to Development");
        if (OperatingSystem.IsWindows())
        {
            var directory = Path.Join(Path.GetTempPath(), "familytree-private-acl-" +
                Guid.NewGuid().ToString("N"));
            try
            {
                PrivateApiComposition.EnsurePrivateDirectory(directory);
                var file = Path.Join(directory, "synthetic.pfx");
                File.WriteAllBytes(file, [1]);
                var permissive = new FileSecurity();
                permissive.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Read, AccessControlType.Allow));
                new FileInfo(file).SetAccessControl(permissive);
                PrivateApiComposition.SetPrivateWindowsFilePermissions(file);
                PrivateApiComposition.EnsureNoReparsePointAncestors(file);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }
        Console.WriteLine("PASS Windows private API environment gate");
    }

    internal static async Task RunAsync()
    {
        var directory = Path.Join(Path.GetTempPath(), "familytree-private-api-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else if (OperatingSystem.IsWindows())
            PrivateApiComposition.EnsurePrivateDirectory(directory);
        var databasePath = Path.Join(directory, "familytree.sqlite");
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var identityKey = RandomNumberGenerator.GetBytes(32);
        var certificatePassword = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var certificatePath = Path.Join(directory, "data-protection.pfx");
        const string authority = "https://accounts.google.com";
        const string adminSubject = "synthetic-private-api-admin";
        const string memberSubject = "synthetic-private-api-member";
        var provider = new InteractiveOidc.Provider("google", "GoogleOidc",
            authority, "synthetic-client", "synthetic-secret", "/auth/callback/google");
        Guid familyId;
        var branchId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid adminId;
        Guid memberId;
        SqliteFamilyStore? registeredFamilyStore = null;
        SqliteTreeReadSource? registeredTreeReadSource = null;
        X509Certificate2? registeredDataProtectionCertificate = null;

        try
        {
            Expect<ArgumentException>(() =>
                    PrivateApiComposition.ResolvePrivateChildPath(directory, "../outside.sqlite"),
                "private database paths cannot escape the configured directory");
            Expect<ArgumentException>(() =>
                    PrivateApiComposition.ResolvePrivateChildPath(directory, "/outside.sqlite"),
                "absolute child paths cannot replace the configured storage directory");
            Expect<ArgumentException>(() =>
                    PrivateApiComposition.ResolvePrivateChildPath("relative", "child"),
                "private database paths require an absolute parent");
            Expect<ArgumentException>(() =>
                    PrivateApiComposition.ResolvePrivateChildPath(directory, " "),
                "private database paths require a child name");
            Expect<ArgumentException>(() =>
                    PrivateApiComposition.EnsureNoReparsePointAncestors("relative-storage"),
                "private storage paths require an absolute path");
            if (OperatingSystem.IsLinux())
            {
                var linkedDirectory = Path.Join(directory, "linked-directory");
                Directory.CreateDirectory(linkedDirectory);
                var directoryLink = Path.Join(directory, "storage-link");
                Directory.CreateSymbolicLink(directoryLink, linkedDirectory);
                Expect<InvalidOperationException>(() =>
                        PrivateApiComposition.EnsureNoReparsePointAncestors(
                            Path.Join(directoryLink, "familytree.sqlite")),
                    "private storage rejects symlinked path components");
            }
            PrivateApiComposition.EnsureNoReparsePointAncestors(
                Path.Join(directory, "missing-parent", "familytree.sqlite"));
            Check(true, "missing storage ancestors are handled without an empty catch");

            CreateSyntheticCertificate(certificatePath, certificatePassword);
            AssertConfigurationFailures(directory, certificatePath, certificatePassword,
                encryptionKey, identityKey, provider);
            using (var registry = new SqlitePrincipalRegistry(databasePath, identityKey))
            {
                adminId = registry.EnrollVerifiedSubject("google", authority, adminSubject);
                memberId = registry.EnrollVerifiedSubject("google", authority, memberSubject);
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
                ["FAMILYTREE_PRIVATE_API_BOOTSTRAP_OWNER_PROVIDER"] = "google",
                ["FAMILYTREE_PRIVATE_API_BOOTSTRAP_OWNER_ISSUER"] = authority,
                ["FAMILYTREE_PRIVATE_API_BOOTSTRAP_OWNER_SUBJECT"] = adminSubject,
                ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"] = "synthetic-client",
                ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"] = "synthetic-secret"
            });
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0,
                    listen => listen.UseHttps(certificatePath, certificatePassword));
            });
            await using var app = ApiApplication.Build(builder);
            await app.StartAsync();
            registeredFamilyStore = app.Services.GetRequiredService<SqliteFamilyStore>();
            registeredTreeReadSource = app.Services.GetRequiredService<SqliteTreeReadSource>();
            registeredDataProtectionCertificate =
                app.Services.GetRequiredService<X509Certificate2>();
            Check(registeredDataProtectionCertificate.HasPrivateKey,
                "host owns the configured Data Protection certificate");
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            Check(addresses is { Count: > 0 }, "private API binds loopback");
            var baseAddress = new Uri(addresses!.Single(
                address => address.StartsWith("https://127.0.0.1:", StringComparison.Ordinal)));
            var httpAddress = new Uri(addresses!.Single(
                address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
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
            using (var httpClient = new HttpClient(new HttpClientHandler
                   {
                       AllowAutoRedirect = false,
                       UseProxy = false,
                       UseCookies = false
                   }) { BaseAddress = httpAddress })
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/csrf"))
            {
                httpRequest.Headers.TryAddWithoutValidation("Cookie", adminCookie);
                using var response = await httpClient.SendAsync(httpRequest);
                Check(response.StatusCode == HttpStatusCode.NotImplemented &&
                    response.Headers.CacheControl?.NoStore == true &&
                    response.Content.Headers.ContentLength == 0,
                    "private API is unavailable over HTTP even from loopback");
            }

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
            using (var unauthorizedBootstrap = await SendWriteAsync(client, HttpMethod.Post,
                       "/api/v1/admin/bootstrap", memberCsrf, memberCookie))
                Check(unauthorizedBootstrap.StatusCode == HttpStatusCode.NotFound &&
                    unauthorizedBootstrap.Content.Headers.ContentLength == 0,
                    "non-owner verified identities cannot bootstrap a family");
            using (var ownerBootstrap = await SendWriteAsync(client, HttpMethod.Post,
                       "/api/v1/admin/bootstrap", adminCsrf, adminCookie))
            {
                var json = await ownerBootstrap.Content.ReadFromJsonAsync<JsonElement>();
                familyId = json.GetProperty("familyId").GetGuid();
                Check(ownerBootstrap.StatusCode == HttpStatusCode.OK &&
                    familyId != Guid.Empty &&
                    app.Services.GetRequiredService<SqliteFamilyStore>()
                        .IsActiveMember(familyId, adminId),
                    "only the configured verified owner bootstraps an active family");
            }
            var bootstrapAuditCount = app.Services.GetRequiredService<SqliteFamilyStore>()
                .CountAudit(familyId, adminId);
            var repeatedBootstraps = await Task.WhenAll(
                SendWriteAsync(client, HttpMethod.Post, "/api/v1/admin/bootstrap",
                    adminCsrf, adminCookie),
                SendWriteAsync(client, HttpMethod.Post, "/api/v1/admin/bootstrap",
                    adminCsrf, adminCookie));
            using (repeatedBootstraps[0])
            using (repeatedBootstraps[1])
            {
                var first = await repeatedBootstraps[0].Content.ReadFromJsonAsync<JsonElement>();
                var second = await repeatedBootstraps[1].Content.ReadFromJsonAsync<JsonElement>();
                Check(repeatedBootstraps.All(response =>
                        response.StatusCode == HttpStatusCode.OK) &&
                    first.GetProperty("familyId").GetGuid() == familyId &&
                    second.GetProperty("familyId").GetGuid() == familyId &&
                    app.Services.GetRequiredService<SqliteFamilyStore>()
                        .CountAudit(familyId, adminId) == bootstrapAuditCount,
                    "concurrent owner bootstrap reuses one family without duplicate audit entries");
            }

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

            using (var pathOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me"))
            {
                pathOrigin.Headers.TryAddWithoutValidation("Cookie",
                    adminCsrf.CookieHeader(adminCookie));
                pathOrigin.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", adminCsrf.RequestToken);
                pathOrigin.Headers.TryAddWithoutValidation("Origin",
                    baseAddress.GetLeftPart(UriPartial.Authority) + "/nested");
                using var response = await client.SendAsync(pathOrigin);
                Check(response.StatusCode == HttpStatusCode.NotFound &&
                    response.Content.Headers.ContentLength == 0,
                    "same-origin requests with a non-root origin path are denied");
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

            var store = app.Services.GetRequiredService<SqliteFamilyStore>();
            var person = new Person(Guid.NewGuid(), "Synthetic HTTP Read Person");
            var proposalId = store.ProposePerson(familyId, memberId, person);
            store.ReviewPerson(familyId, adminId, proposalId, approve: true);
            var grants = new SqliteGraphGrantStore(databasePath);
            grants.GrantPerson(familyId, branchId, adminId, memberId, person.Id);
            grants.GrantDisplayName(familyId, branchId, adminId, memberId, person.Id);
            var personPath = $"/api/v1/families/{familyId:D}/branches/{branchId:D}/people/{person.Id:D}";
            using (var personResponse = await GetWithCookieAsync(client, personPath, memberCookie))
            {
                var json = await personResponse.Content.ReadFromJsonAsync<JsonElement>();
                Check(personResponse.StatusCode == HttpStatusCode.OK &&
                    json.GetProperty("displayName").GetString() == person.DisplayName &&
                    personResponse.Headers.CacheControl?.NoStore == true,
                    "verified member reads a person only after explicit persisted grants");
            }
            var searchPath = $"/api/v1/families/{familyId:D}/branches/{branchId:D}/search?q=Synthetic";
            using (var searchResponse = await GetWithCookieAsync(client, searchPath, memberCookie))
            {
                var results = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
                Check(searchResponse.StatusCode == HttpStatusCode.OK &&
                    results.GetArrayLength() == 1 &&
                    results[0].GetProperty("displayName").GetString() == person.DisplayName,
                    "authorized person search returns only granted fields");
            }
            using (var emptySearch = await GetWithCookieAsync(client,
                       $"/api/v1/families/{familyId:D}/branches/{branchId:D}/search?q=absent",
                       memberCookie))
            {
                var results = await emptySearch.Content.ReadFromJsonAsync<JsonElement>();
                Check(emptySearch.StatusCode == HttpStatusCode.OK &&
                    results.GetArrayLength() == 0,
                    "authorized search returns an empty result without disclosing hidden people");
            }
            var treePath = $"/api/v1/families/{familyId:D}/branches/{branchId:D}/tree/{person.Id:D}";
            using (var treeResponse = await GetWithCookieAsync(client, treePath, memberCookie))
            {
                var tree = await treeResponse.Content.ReadFromJsonAsync<JsonElement>();
                Check(treeResponse.StatusCode == HttpStatusCode.OK &&
                    tree.GetProperty("nodes").GetArrayLength() == 1 &&
                    tree.GetProperty("edges").GetArrayLength() == 0,
                    "authorized tree read contains only its transactionally granted center");
            }
            using (var forgedRead = await GetWithCookieAsync(client, personPath,
                       "__Host-FamilyTreeSession=forged"))
                Check(forgedRead.StatusCode == HttpStatusCode.NotFound &&
                    forgedRead.Content.Headers.ContentLength == 0,
                    "forged OIDC cookies cannot access private reads");
            var unregisteredCookie = await CreateCookieAsync(app, provider,
                "synthetic-unregistered-read-principal");
            using (var unregisteredRead = await GetWithCookieAsync(client, personPath,
                       unregisteredCookie))
                Check(unregisteredRead.StatusCode == HttpStatusCode.NotFound &&
                    unregisteredRead.Content.Headers.ContentLength == 0,
                    "valid OIDC sessions without a registered principal cannot read records");
            using (var forbidden = await GetWithCookieAsync(client,
                       $"/api/v1/families/{familyId:D}/branches/{Guid.NewGuid():D}/people/{person.Id:D}",
                       memberCookie))
            using (var unknown = await GetWithCookieAsync(client,
                       $"/api/v1/families/{familyId:D}/branches/{branchId:D}/people/{Guid.NewGuid():D}",
                       memberCookie))
                Check(forbidden.StatusCode == HttpStatusCode.NotFound &&
                    unknown.StatusCode == forbidden.StatusCode &&
                    await forbidden.Content.ReadAsStringAsync() ==
                    await unknown.Content.ReadAsStringAsync(),
                    "hidden and unknown person responses are empty and indistinguishable");

            var revokePath = $"/api/v1/families/{familyId:D}/members/{memberId:D}";
            using (var revoked = await SendWriteAsync(client, HttpMethod.Delete,
                       revokePath, adminCsrf, adminCookie))
                Check(revoked.StatusCode == HttpStatusCode.NoContent,
                    "active administrator can revoke a member");
            Check(!app.Services.GetRequiredService<SqliteFamilyStore>()
                .IsActiveMember(familyId, memberId), "revocation takes effect immediately");
            using (var revokedRead = await GetWithCookieAsync(client, personPath, memberCookie))
                Check(revokedRead.StatusCode == HttpStatusCode.NotFound &&
                    revokedRead.Content.Headers.ContentLength == 0,
                    "revoked member loses HTTP read access immediately");

            grants.GrantPerson(familyId, branchId, adminId, adminId, person.Id);
            grants.GrantDisplayName(familyId, branchId, adminId, adminId, person.Id);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                       { DataSource = databasePath, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE people SET encrypted_name = X'00' WHERE family_id=$family AND id=$person;";
                command.Parameters.AddWithValue("$family", familyId.ToString("D"));
                command.Parameters.AddWithValue("$person", person.Id.ToString("D"));
                command.ExecuteNonQuery();
            }
            using (var failedRead = await GetWithCookieAsync(client,
                       $"/api/v1/families/{familyId:D}/branches/{branchId:D}/people/{person.Id:D}",
                       adminCookie))
            using (var unknownRead = await GetWithCookieAsync(client,
                       $"/api/v1/families/{familyId:D}/branches/{branchId:D}/people/{Guid.NewGuid():D}",
                       adminCookie))
                Check(failedRead.StatusCode == HttpStatusCode.NotFound &&
                    failedRead.Content.Headers.ContentLength == 0 &&
                    unknownRead.StatusCode == failedRead.StatusCode &&
                    await unknownRead.Content.ReadAsStringAsync() ==
                    await failedRead.Content.ReadAsStringAsync(),
                    "read exceptions normalize to the same empty denial as unknown records");

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
            var unavailableRelationshipRoutes = new[]
            {
                $"/api/v1/families/{familyId:D}/relationships/proposals",
                $"/api/v1/families/{familyId:D}/relationships/pending/{invitationId:D}/review",
                $"/api/v1/families/{familyId:D}/relationships/approved/{invitationId:D}/undo"
            };
            foreach (var route in unavailableRelationshipRoutes)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route)
                {
                    Content = JsonContent.Create(new { approve = true })
                };
                request.Headers.TryAddWithoutValidation("Cookie", adminCookie);
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.NotImplemented &&
                    await response.Content.ReadAsStringAsync() == "" &&
                    response.Headers.CacheControl?.NoStore == true,
                    $"unscoped #94 relationship handlers remain closed: {route}");
            }

            await app.StopAsync();
            await app.DisposeAsync();
            Expect<ObjectDisposedException>(() =>
                    registeredFamilyStore.IsActiveMember(familyId, memberId),
                "host disposal releases registered SQLite stores");
            Expect<ObjectDisposedException>(() =>
                    registeredTreeReadSource.LoadAsync(adminId, familyId, branchId,
                        Guid.NewGuid(), default).GetAwaiter().GetResult(),
                "host disposal releases the trusted read source");
            ExpectCertificateDisposed(registeredDataProtectionCertificate);
            using (var reopenedFamilyStore = new SqliteFamilyStore(databasePath, encryptionKey))
                Check(reopenedFamilyStore.CreateFamilyForBootstrapOwner(adminId) == familyId,
                    "owner bootstrap remains idempotent after reopening the SQLite database");
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
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else if (OperatingSystem.IsWindows())
            PrivateApiComposition.SetPrivateWindowsFilePermissions(path);
    }

    private static void AssertConfigurationFailures(string directory,
        string certificatePath, string certificatePassword, byte[] encryptionKey,
        byte[] identityKey, InteractiveOidc.Provider provider)
    {
        var valid = new Dictionary<string, string?>
        {
            ["FAMILYTREE_PRIVATE_API_ENABLED"] = "true",
            ["FAMILYTREE_PRIVATE_API_DATA_DIRECTORY"] = directory,
            ["FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY"] =
                Convert.ToBase64String(encryptionKey),
            ["FAMILYTREE_PRIVATE_API_IDENTITY_KEY"] = Convert.ToBase64String(identityKey),
            ["FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE"] = certificatePath,
            ["FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD"] =
                certificatePassword
        };
        ExpectConfigurationFailure(valid, [provider], "Production",
            "private API refuses non-Development hosting");
        ExpectConfigurationFailure(valid, [], Environments.Development,
            "private API requires an OIDC provider");
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_BOOTSTRAP_OWNER_PROVIDER", "google"),
            [provider], Environments.Development,
            "private API rejects incomplete bootstrap-owner configuration");
        var noBootstrapOwner = PrivateApiBootstrapOwner.Read(
            new ConfigurationBuilder().Build(), [provider]);
        Check(!noBootstrapOwner.IsConfigured &&
            !noBootstrapOwner.Authorizes(("google", provider.Authority, "synthetic-owner")),
            "bootstrap remains unavailable without an explicit owner allowlist");
        ExpectConfigurationFailure(Remove(valid,
                "FAMILYTREE_PRIVATE_API_DATA_DIRECTORY"), [provider],
            Environments.Development, "private API requires an absolute storage directory");
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_DATA_DIRECTORY", "relative-storage"),
            [provider], Environments.Development,
            "private API rejects relative storage directories");
        ExpectConfigurationFailure(Remove(valid,
                "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE"),
            [provider], Environments.Development,
            "private API requires an absolute certificate path");
        ExpectConfigurationFailure(Remove(valid,
                "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD"),
            [provider], Environments.Development,
            "private API requires a certificate password");
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY", "not-base64"),
            [provider], Environments.Development,
            "private API rejects malformed encryption keys");
        ExpectConfigurationFailure(Remove(valid,
                "FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY"),
            [provider], Environments.Development,
            "private API rejects missing encryption keys");
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY",
                Convert.ToBase64String(new byte[31])),
            [provider], Environments.Development,
            "private API rejects short encryption keys");
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_IDENTITY_KEY", "not-base64"),
            [provider], Environments.Development,
            "private API rejects malformed identity keys");
        ExpectConfigurationFailure(Remove(valid,
                "FAMILYTREE_PRIVATE_API_IDENTITY_KEY"),
            [provider], Environments.Development,
            "private API rejects missing identity keys");
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE",
                Path.GetFullPath(Path.Join(directory, "..", "outside.pfx"))),
            [provider], Environments.Development,
            "private API rejects certificates outside private storage");
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE",
                Path.Join(directory, "missing.pfx")),
            [provider], Environments.Development,
            "private API rejects missing certificate files");
        var expiredCertificate = CreateInvalidCertificate(directory,
            certificatePassword, keySize: 2048, expired: true);
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE",
                expiredCertificate),
            [provider], Environments.Development,
            "private API rejects expired Data Protection certificates");
        var weakCertificate = CreateInvalidCertificate(directory,
            certificatePassword, keySize: 1024, expired: false);
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE",
                weakCertificate),
            [provider], Environments.Development,
            "private API rejects weak Data Protection certificates");
        var publicOnlyCertificate = CreatePublicOnlyCertificate(directory,
            certificatePassword);
        ExpectConfigurationFailure(Replace(valid,
                "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE",
                publicOnlyCertificate),
            [provider], Environments.Development,
            "private API rejects certificates without a private key");
        Expect<CryptographicException>(() =>
                ConfigureForFailure(Replace(valid,
                    "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD",
                    "wrong-password"), [provider]),
            "private API rejects incorrect certificate passwords");

        var disabled = WebApplication.CreateBuilder(new WebApplicationOptions
            { EnvironmentName = Environments.Development });
        disabled.Configuration.Sources.Clear();
        Check(!PrivateApiComposition.Configure(disabled, []),
            "private API remains disabled by default");
        using (var services = disabled.Services.BuildServiceProvider())
        {
            Expect<InvalidOperationException>(() =>
                    services.GetRequiredService<VerifiedFamilyMembership>(),
                "disabled membership service fails closed");
            Expect<InvalidOperationException>(() =>
                    services.GetRequiredService<SqlitePersonReadService>(),
                "disabled person read service fails closed");
            Expect<InvalidOperationException>(() =>
                    services.GetRequiredService<PrivateTreeReadService>(),
                "disabled tree read service fails closed");
        }

        if (OperatingSystem.IsLinux())
        {
            var permissiveDirectory = Path.Join(directory, "permissive");
            Directory.CreateDirectory(permissiveDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            File.SetUnixFileMode(permissiveDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            try
            {
                Expect<InvalidOperationException>(() =>
                        PrivateApiComposition.EnsurePrivateDirectory(permissiveDirectory),
                    "private API rejects group-readable storage");
            }
            finally
            {
                File.SetUnixFileMode(permissiveDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                Directory.Delete(permissiveDirectory);
            }
            var originalCertificateMode = File.GetUnixFileMode(certificatePath);
            File.SetUnixFileMode(certificatePath,
                UnixFileMode.UserRead | UnixFileMode.GroupRead);
            try
            {
                ExpectConfigurationFailure(valid, [provider], Environments.Development,
                    "private API rejects group-readable certificates");
            }
            finally
            {
                File.SetUnixFileMode(certificatePath, originalCertificateMode);
            }
        }
    }

    private static Dictionary<string, string?> Remove(
        IReadOnlyDictionary<string, string?> values, string key)
    {
        var result = new Dictionary<string, string?>(values);
        result.Remove(key);
        return result;
    }

    private static Dictionary<string, string?> Replace(
        IReadOnlyDictionary<string, string?> values, string key, string value)
    {
        var result = new Dictionary<string, string?>(values) { [key] = value };
        return result;
    }

    private static void ExpectConfigurationFailure(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<InteractiveOidc.Provider> providers,
        string environment, string message)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        Expect<InvalidOperationException>(() =>
                PrivateApiComposition.Configure(builder, providers), message);
    }

    private static void ConfigureForFailure(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<InteractiveOidc.Provider> providers)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { EnvironmentName = Environments.Development });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        PrivateApiComposition.Configure(builder, providers);
    }

    private static string CreateInvalidCertificate(string directory, string password,
        int keySize, bool expired)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest("CN=FamilyTree synthetic invalid test",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            expired ? DateTimeOffset.UtcNow.AddDays(-2) : DateTimeOffset.UtcNow.AddMinutes(-1),
            expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Join(directory, $"invalid-{keySize}-{expired}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else if (OperatingSystem.IsWindows())
            PrivateApiComposition.SetPrivateWindowsFilePermissions(path);
        return path;
    }

    private static string CreatePublicOnlyCertificate(string directory, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FamilyTree synthetic public-only test",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(
            certificate.Export(X509ContentType.Cert));
        var path = Path.Join(directory, "public-only.pfx");
        File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pfx, password));
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else if (OperatingSystem.IsWindows())
            PrivateApiComposition.SetPrivateWindowsFilePermissions(path);
        return path;
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

    private static async Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client,
        string path, string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
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

    private static void Expect<TException>(Action action, string message)
        where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void ExpectCertificateDisposed(X509Certificate2 certificate)
    {
        try
        {
            using var privateKey = certificate.GetRSAPrivateKey();
            if (privateKey is null) return;
        }
        catch (CryptographicException) { return; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException(
            "Host disposal must release its Data Protection certificate.");
    }
}
