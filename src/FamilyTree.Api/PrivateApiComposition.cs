using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using FamilyTree.Domain;
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
            builder.Services.AddSingleton<SqlitePersonReadService>(_ =>
                throw new InvalidOperationException("The private API is disabled."));
            builder.Services.AddSingleton<PrivateTreeReadService>(_ =>
                throw new InvalidOperationException("The private API is disabled."));
            builder.Services.AddSingleton<SqliteFamilyStore>(_ =>
                throw new InvalidOperationException("The private API is disabled."));
            builder.Services.AddSingleton<PrivateApiBootstrapOwner>(_ =>
                throw new InvalidOperationException("The private API is disabled."));
            return false;
        }

        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
        if (!builder.Environment.IsDevelopment() ||
            !(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
            throw new InvalidOperationException(EnvironmentBlocker(
                builder.Environment.IsDevelopment(), OperatingSystem.IsLinux(),
                OperatingSystem.IsWindows()));
        if (providers.Count == 0)
            throw new InvalidOperationException(
                "The private API requires an explicitly configured OIDC provider.");
        var bootstrapOwner = PrivateApiBootstrapOwner.Read(builder.Configuration, providers);
        builder.Services.AddSingleton(bootstrapOwner);

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
        using var resources = new PrivateApiResourceLifetime();
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
            if (OperatingSystem.IsWindows())
            {
                if (!File.Exists(databasePath))
                    using (new FileStream(databasePath, FileMode.CreateNew,
                               FileAccess.ReadWrite, FileShare.None)) { }
                SetPrivateWindowsFilePermissions(databasePath);
            }
            var familyStore = resources.Track(
                new SqliteFamilyStore(databasePath, encryptionKey));
            var registry = resources.Track(
                new SqlitePrincipalRegistry(databasePath, identityKey));
            var readSource = resources.Track(
                new SqliteTreeReadSource(databasePath, encryptionKey));
            var isLinux = OperatingSystem.IsLinux();
            var isWindows = OperatingSystem.IsWindows();
            if (isLinux)
                File.SetUnixFileMode(databasePath, PrivateFileMode);
            if (isWindows) SetPrivateWindowsFilePermissions(databasePath);
            if (!isLinux && !isWindows)
                throw new InvalidOperationException("Unsupported private API storage platform.");
            builder.Services.AddSingleton<SqliteFamilyStore>(_ => familyStore);
            builder.Services.AddSingleton<SqlitePrincipalRegistry>(_ => registry);
            builder.Services.AddSingleton<SqliteTreeReadSource>(_ => readSource);
            builder.Services.AddSingleton<SqlitePersonReadService>();
            builder.Services.AddSingleton<PrivateTreeReadService>(_ =>
                new PrivateTreeReadService(readSource));
            builder.Services.AddSingleton(new VerifiedFamilyMembership(
                registry, familyStore, providers));

            var dataProtectionCertificate = resources.Track(
                new X509Certificate2(certificate));
            builder.Services.AddSingleton<X509Certificate2>(_ => dataProtectionCertificate);
            builder.Services.AddDataProtection()
                .SetApplicationName("FamilyTree.Api.Private.Local")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
                .ProtectKeysWithCertificate(dataProtectionCertificate);
            resources.TransferToHost();
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

    private sealed class PrivateApiResourceLifetime : IDisposable
    {
        private readonly List<IDisposable> _resources = [];
        private bool _transferred;

        internal T Track<T>(T resource) where T : IDisposable
        {
            _resources.Add(resource);
            return resource;
        }

        internal void TransferToHost() => _transferred = true;

        public void Dispose()
        {
            if (_transferred) return;
            for (var index = _resources.Count - 1; index >= 0; index--)
                _resources[index].Dispose();
        }
    }

    internal static string EnvironmentBlocker(bool isDevelopment, bool isLinux,
        bool isWindows = false) =>
        isDevelopment && (isLinux || isWindows)
            ? string.Empty
            : "The private API requires local Development on Linux or Windows.";

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

    internal static void EnsurePrivateDirectory(string path)
    {
        var isLinux = OperatingSystem.IsLinux();
        var isWindows = OperatingSystem.IsWindows();
        if (isLinux) EnsurePrivateLinuxDirectory(path);
        if (isWindows) EnsurePrivateWindowsDirectory(path);
        if (!isLinux && !isWindows)
            throw new InvalidOperationException("Unsupported private API storage platform.");
    }

    [SupportedOSPlatform("linux")]
    private static void EnsurePrivateLinuxDirectory(string path)
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

    [SupportedOSPlatform("windows")]
    private static void EnsurePrivateWindowsDirectory(string path)
    {
        EnsureNoReparsePointAncestors(path);
        var security = CreatePrivateWindowsDirectorySecurity();
        var directory = new DirectoryInfo(path);
        directory.Create(security);
        directory.SetAccessControl(security);
        EnsureNoReparsePointAncestors(path);
        VerifyPrivateWindowsAccess(directory.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreatePrivateWindowsDirectorySecurity()
    {
        var sid = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    internal static void SetPrivateWindowsFilePermissions(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid,
            FileSystemRights.FullControl, AccessControlType.Allow));
        var file = new FileInfo(path);
        file.SetAccessControl(security);
        VerifyPrivateWindowsAccess(file.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyPrivateWindowsAccess(FileSystemSecurity security)
    {
        var sid = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var rules = security.GetAccessRules(includeExplicit: true,
                includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected ||
            !sid.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
            rules.Length == 0 ||
            rules.Any(rule => !sid.Equals(rule.IdentityReference) ||
                              rule.AccessControlType != AccessControlType.Allow))
            throw new InvalidOperationException(
                "Private API storage must grant access only to the current Windows user.");
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
            if (TryGetAttributes(current, out var attributes) &&
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Private API storage cannot use symbolic links or reparse points.");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

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
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(fullPath);
            if ((mode & UnixFileMode.UserRead) == 0 ||
                (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                         UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                         UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException(
                    "The Data Protection certificate must be readable only by the current user.");
        }
        if (OperatingSystem.IsWindows()) SetPrivateWindowsFilePermissions(fullPath);
        return fullPath;
    }
}

internal sealed record PrivateApiBootstrapOwner(
    bool IsConfigured, string Provider, string Issuer, string Subject)
{
    internal static PrivateApiBootstrapOwner Read(IConfiguration configuration,
        IReadOnlyList<InteractiveOidc.Provider> providers)
    {
        var provider = configuration["FAMILYTREE_PRIVATE_API_BOOTSTRAP_OWNER_PROVIDER"];
        var issuer = configuration["FAMILYTREE_PRIVATE_API_BOOTSTRAP_OWNER_ISSUER"];
        var subject = configuration["FAMILYTREE_PRIVATE_API_BOOTSTRAP_OWNER_SUBJECT"];
        var configuredValues = new[] { provider, issuer, subject };
        if (configuredValues.All(string.IsNullOrWhiteSpace))
            return new(false, string.Empty, string.Empty, string.Empty);
        if (configuredValues.Any(string.IsNullOrWhiteSpace) ||
            subject!.Length > 256 || subject.Any(char.IsControl) ||
            subject != subject.Trim() ||
            !providers.Any(candidate =>
                string.Equals(candidate.Name, provider, StringComparison.Ordinal) &&
                string.Equals(candidate.Authority, issuer, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Private API bootstrap owner must match a configured OIDC provider and subject.");
        return new(true, provider!, issuer!, subject);
    }

    internal bool Authorizes((string Provider, string Issuer, string Subject) identity) =>
        IsConfigured &&
        string.Equals(identity.Provider, Provider, StringComparison.Ordinal) &&
        string.Equals(identity.Issuer, Issuer, StringComparison.Ordinal) &&
        string.Equals(identity.Subject, Subject, StringComparison.Ordinal);
}

internal static class PrivateApiEndpoints
{
    internal sealed record InvitationRequest(Guid IntendedPrincipalId);
    private const string FamilyBranchPath =
        "/api/v1/families/{familyId:guid}/branches/{branchId:guid}";

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

        app.MapGet("/api/v1/me", async (HttpContext context,
            VerifiedFamilyMembership membership) =>
        {
            var principalId = await GetRegisteredViewerAsync(context, providers, membership);
            return principalId is { } id
                ? Results.Ok(new { principalId = id })
                : Results.StatusCode(StatusCodes.Status404NotFound);
        }).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapGet("/api/v1/families", async (HttpContext context,
            VerifiedFamilyMembership membership, SqliteFamilyStore families) =>
        {
            var principalId = await GetRegisteredViewerAsync(context, providers, membership);
            return principalId is { } id
                ? Results.Ok(families.ListActiveFamilyIds(id))
                : Results.StatusCode(StatusCodes.Status404NotFound);
        }).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapGet("/api/v1/families/{familyId:guid}/branches",
            async (Guid familyId, HttpContext context,
                VerifiedFamilyMembership membership, SqliteFamilyStore families) =>
            {
                var principalId = await GetRegisteredViewerAsync(context, providers, membership);
                return principalId is { } id
                    ? Results.Ok(families.ListGrantedBranchIds(familyId, id))
                    : Results.StatusCode(StatusCodes.Status404NotFound);
            }).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapGet("/api/v1/families/{familyId:guid}/members",
            async (Guid familyId, HttpContext context,
                VerifiedFamilyMembership membership, SqliteFamilyStore families) =>
            {
                var principalId = await GetRegisteredViewerAsync(context, providers, membership);
                return principalId is { } id
                    ? Results.Ok(families.ListMembersForAdministrator(familyId, id))
                    : Results.StatusCode(StatusCodes.Status404NotFound);
            }).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapGet($"{FamilyBranchPath}/people/{{personId:guid}}",
            async (Guid familyId, Guid branchId, Guid personId, HttpContext context,
                VerifiedFamilyMembership membership, SqlitePersonReadService people) =>
            {
                var viewer = await GetRegisteredViewerAsync(context, providers, membership);
                if (viewer is null)
                    return Results.StatusCode(StatusCodes.Status404NotFound);
                var person = await people.GetAsync(viewer.Value, familyId, branchId, personId,
                    context.RequestAborted);
                return person is null ? Results.StatusCode(StatusCodes.Status404NotFound) :
                    Results.Ok(person);
            }).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapGet($"{FamilyBranchPath}/search",
            async (Guid familyId, Guid branchId, HttpContext context,
                VerifiedFamilyMembership membership, SqlitePersonReadService people) =>
            {
                var viewer = await GetRegisteredViewerAsync(context, providers, membership);
                if (viewer is null)
                    return Results.StatusCode(StatusCodes.Status404NotFound);
                var results = await people.SearchAsync(viewer.Value, familyId, branchId,
                    context.Request.Query["q"], context.RequestAborted);
                return Results.Ok(results);
            }).WithMetadata(PrivateApiBoundary.EnabledPrivateEndpoint.Instance);

        app.MapGet($"{FamilyBranchPath}/tree/{{centerId:guid}}",
            async (Guid familyId, Guid branchId, Guid centerId, HttpContext context,
                VerifiedFamilyMembership membership, PrivateTreeReadService trees) =>
            {
                var viewer = await GetRegisteredViewerAsync(context, providers, membership);
                if (viewer is null)
                    return Results.StatusCode(StatusCodes.Status404NotFound);
                var tree = await trees.GetAsync(viewer.Value, familyId, branchId, centerId,
                    new TreeOptions(), context.RequestAborted);
                return tree is null ? Results.StatusCode(StatusCodes.Status404NotFound) :
                    Results.Ok(tree);
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

        app.MapPost("/api/v1/admin/bootstrap", async (HttpContext context,
            VerifiedFamilyMembership membership, SqliteFamilyStore families,
            PrivateApiBootstrapOwner bootstrapOwner, IAntiforgery antiforgery) =>
            await ExecuteWriteAsync(context, providers, antiforgery, async () =>
            {
                var identity = await VerifiedOidcSession.ReadBindingAsync(context, providers);
                if (identity is not { } verifiedIdentity ||
                    !bootstrapOwner.Authorizes(verifiedIdentity))
                    return Results.StatusCode(StatusCodes.Status404NotFound);
                var administratorId = await membership.RegisterAsync(context);
                if (administratorId is not { } principalId)
                    return Results.StatusCode(StatusCodes.Status404NotFound);
                var familyId = families.CreateFamilyForBootstrapOwner(principalId);
                return Results.Ok(new { familyId });
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

    private static async Task<Guid?> GetRegisteredViewerAsync(HttpContext context,
        IReadOnlyList<InteractiveOidc.Provider> providers,
        VerifiedFamilyMembership membership)
    {
        if (!await VerifiedOidcSession.ReadAsync(context, providers))
            return null;
        return await membership.FindRegisteredPrincipalAsync(context);
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
