using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FamilyTree.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace FamilyTree.Api;

internal static class PrivateApiComposition
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static bool Configure(WebApplicationBuilder builder,
        IReadOnlyList<InteractiveOidc.Provider> providers)
    {
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-FamilyTreeAntiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });

        if (!string.Equals(builder.Configuration["FAMILYTREE_PRIVATE_API_ENABLED"],
                "true", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<VerifiedFamilyMembership>(_ =>
                throw new InvalidOperationException("The private API is disabled."));
            return false;
        }

        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
        if (!builder.Environment.IsDevelopment() || !OperatingSystem.IsLinux())
            throw new InvalidOperationException(EnvironmentBlocker(
                builder.Environment.IsDevelopment(), OperatingSystem.IsLinux()));
        if (providers.Count == 0)
            throw new InvalidOperationException(
                "The private API requires an explicitly configured OIDC provider.");

        var configuredDirectory = builder.Configuration["FAMILYTREE_PRIVATE_API_DATA_DIRECTORY"];
        if (string.IsNullOrWhiteSpace(configuredDirectory) ||
            !Path.IsPathFullyQualified(configuredDirectory))
            throw new InvalidOperationException(
                "An absolute private API data directory is required.");
        var dataDirectory = Path.GetFullPath(configuredDirectory);
        EnsurePrivateDirectory(dataDirectory);
        var keyDirectory = ResolvePrivateChildPath(dataDirectory, "data-protection");
        EnsurePrivateDirectory(keyDirectory);
        var certificatePath = ReadCertificatePath(builder.Configuration, dataDirectory);
        var certificatePassword = builder.Configuration[
            "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD"];
        if (string.IsNullOrWhiteSpace(certificatePassword))
            throw new InvalidOperationException(
                "A Data Protection certificate password is required.");

        byte[]? encryptionKey = null;
        byte[]? identityKey = null;
        try
        {
            encryptionKey = ReadKey(builder.Configuration["FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY"]);
            identityKey = ReadKey(builder.Configuration["FAMILYTREE_PRIVATE_API_IDENTITY_KEY"]);
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath,
                certificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            using (var privateKey = certificate.GetRSAPrivateKey())
                if (privateKey is null || privateKey.KeySize < 2048 ||
                    DateTimeOffset.UtcNow < certificate.NotBefore ||
                    DateTimeOffset.UtcNow > certificate.NotAfter)
                    throw new InvalidOperationException(
                        "A current RSA Data Protection certificate is required.");

            var databasePath = ResolvePrivateChildPath(dataDirectory, "familytree.sqlite");
            EnsureNoReparsePointAncestors(databasePath);
            var familyStore = new SqliteFamilyStore(databasePath, encryptionKey);
            SqlitePrincipalRegistry? registry = null;
            try
            {
                registry = new SqlitePrincipalRegistry(databasePath, identityKey);
                File.SetUnixFileMode(databasePath, PrivateFileMode);
                builder.Services.AddSingleton<SqliteFamilyStore>(_ => familyStore);
                builder.Services.AddSingleton<SqlitePrincipalRegistry>(_ => registry);
                builder.Services.AddSingleton(new VerifiedFamilyMembership(
                    registry, familyStore, providers));
            }
            catch
            {
                registry?.Dispose();
                familyStore.Dispose();
                throw;
            }

            var dataProtectionCertificate = new X509Certificate2(certificate);
            builder.Services.AddSingleton<X509Certificate2>(_ => dataProtectionCertificate);
            builder.Services.AddDataProtection()
                .SetApplicationName("FamilyTree.Api.Private.Local")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
                .ProtectKeysWithCertificate(dataProtectionCertificate);
            return true;
        }
        finally
        {
            if (encryptionKey is not null)
                CryptographicOperations.ZeroMemory(encryptionKey);
            if (identityKey is not null)
                CryptographicOperations.ZeroMemory(identityKey);
        }
    }

    internal static string EnvironmentBlocker(bool isDevelopment, bool isLinux) =>
        isDevelopment && isLinux
            ? string.Empty
            : "The private API currently requires local Linux Development; Windows and non-Development hosting are blocked.";

    internal static string ResolvePrivateChildPath(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathFullyQualified(parent) ||
            string.IsNullOrWhiteSpace(child) || Path.IsPathFullyQualified(child))
            throw new ArgumentException("A private child path is required.");

        var normalizedParent = Path.GetFullPath(parent);
        var fullPath = Path.GetFullPath(Path.Join(normalizedParent, child));
        var relativePath = Path.GetRelativePath(normalizedParent, fullPath);
        if (relativePath == "." || relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relativePath))
            throw new ArgumentException("The private path must remain below its configured directory.");
        return fullPath;
    }

    private static byte[] ReadKey(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("Private API key configuration is incomplete.");
        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Private API key configuration is invalid.", exception);
        }
        if (key.Length == 32)
            return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidOperationException("Private API keys must contain 256 bits.");
    }

    [SupportedOSPlatform("linux")]
    private static void EnsurePrivateDirectory(string path)
    {
        EnsureNoReparsePointAncestors(path);
        Directory.CreateDirectory(path, PrivateDirectoryMode);
        EnsureNoReparsePointAncestors(path);
        var mode = File.GetUnixFileMode(path);
        if ((mode & PrivateDirectoryMode) != PrivateDirectoryMode ||
            (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                     UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                     UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException(
                "Private API directories must be accessible only to the current user.");
    }

    internal static void EnsureNoReparsePointAncestors(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute storage path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath)!;
        foreach (var segment in fullPath[current.Length..]
                     .Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        "Private API storage cannot use symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    [SupportedOSPlatform("linux")]
    private static string ReadCertificatePath(IConfiguration configuration,
        string dataDirectory)
    {
        var configuredPath = configuration[
            "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE"];
        if (string.IsNullOrWhiteSpace(configuredPath) ||
            !Path.IsPathFullyQualified(configuredPath))
            throw new InvalidOperationException(
                "An absolute Data Protection certificate path is required.");
        var fullPath = Path.GetFullPath(configuredPath);
        var relativePath = Path.GetRelativePath(dataDirectory, fullPath);
        EnsureNoReparsePointAncestors(fullPath);
        if (relativePath == "." || relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relativePath) || !File.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "The Data Protection certificate must be a regular file in private storage.");
        var mode = File.GetUnixFileMode(fullPath);
        if ((mode & UnixFileMode.UserRead) == 0 ||
            (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                     UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                     UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException(
                "The Data Protection certificate must be readable only by the current user.");
        return fullPath;
    }
}

internal static class PrivateApiEndpoints
{
    internal sealed record InvitationRequest(Guid IntendedPrincipalId);

    internal static void MapPrivateApiEndpoints(this WebApplication app,
        IReadOnlyList<InteractiveOidc.Provider> providers)
    {
        app.MapGet("/api/v1/csrf", async (HttpContext context,
            IAntiforgery antiforgery) =>
        {
            if (!await VerifiedOidcSession.ReadAsync(context, providers))
                return Results.StatusCode(StatusCodes.Status404NotFound);
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { requestToken = tokens.RequestToken });
        }).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapPost("/api/v1/me", async (HttpContext context,
            VerifiedFamilyMembership membership, IAntiforgery antiforgery) =>
            await ExecuteWriteAsync(context, providers, antiforgery, async () =>
            {
                var principalId = await membership.RegisterAsync(context);
                return principalId is { } id
                    ? Results.Ok(new { principalId = id })
                    : Results.StatusCode(StatusCodes.Status404NotFound);
            })).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapPost("/api/v1/families/{familyId:guid}/invitations",
            async (Guid familyId, InvitationRequest request, HttpContext context,
                VerifiedFamilyMembership membership, IAntiforgery antiforgery) =>
                await ExecuteWriteAsync(context, providers, antiforgery, async () =>
                {
                    var invitationId = await membership.IssueInvitationAsync(context,
                        familyId, request.IntendedPrincipalId,
                        DateTimeOffset.UtcNow.AddDays(7));
                    return Results.Ok(new { invitationId });
                })).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapPost("/api/v1/families/{familyId:guid}/invitations/{invitationId:guid}/accept",
            async (Guid familyId, Guid invitationId, HttpContext context,
                VerifiedFamilyMembership membership, IAntiforgery antiforgery) =>
                await ExecuteWriteAsync(context, providers, antiforgery, async () =>
                {
                    await membership.AcceptInvitationAsync(context, familyId, invitationId);
                    return Results.NoContent();
                })).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapPost("/api/v1/families/{familyId:guid}/members/{memberId:guid}/approve",
            async (Guid familyId, Guid memberId, HttpContext context,
                VerifiedFamilyMembership membership, IAntiforgery antiforgery) =>
                await ExecuteWriteAsync(context, providers, antiforgery, async () =>
                {
                    await membership.ApproveMemberAsync(context, familyId, memberId);
                    return Results.NoContent();
                })).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapDelete("/api/v1/families/{familyId:guid}/members/{memberId:guid}",
            async (Guid familyId, Guid memberId, HttpContext context,
                VerifiedFamilyMembership membership, IAntiforgery antiforgery) =>
                await ExecuteWriteAsync(context, providers, antiforgery, async () =>
                {
                    await membership.RevokeMemberAsync(context, familyId, memberId);
                    return Results.NoContent();
                })).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);
    }

    private static async Task<IResult> ExecuteWriteAsync(HttpContext context,
        IReadOnlyList<InteractiveOidc.Provider> providers, IAntiforgery antiforgery,
        Func<Task<IResult>> operation)
    {
        if (!await VerifiedOidcSession.ReadAsync(context, providers) ||
            !HasSameOrigin(context))
            return Results.StatusCode(StatusCodes.Status404NotFound);

        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return await operation();
        }
        catch (AntiforgeryValidationException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                           InvalidOperationException or ArgumentException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
    }

    private static bool HasSameOrigin(HttpContext context)
    {
        if (!Uri.TryCreate(context.Request.Headers.Origin, UriKind.Absolute, out var origin))
            return false;
        return origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" &&
            origin.Query.Length == 0 && origin.Fragment.Length == 0 &&
            string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.Authority, context.Request.Host.Value,
                StringComparison.OrdinalIgnoreCase);
    }
}
