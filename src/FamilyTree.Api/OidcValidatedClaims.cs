using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FamilyTree.Domain;

namespace FamilyTree.Api;

/// <summary>
/// An OIDC identity remains separate from a genealogy Person. Only the OIDC
/// OnTokenValidated callback may stamp these markers; cookie middleware
/// authenticates and signs them before later requests consume them.
/// A verified login alone grants ZERO family or field permissions.
/// </summary>
public sealed record VerifiedExternalIdentity(
    string Provider, string Issuer, string Audience, string Subject);

public static class OidcValidatedClaims
{
    public const string InternalIssuer = "FamilyTree.VerifiedOIDC";
    public const string ProviderType = "familytree:provider";
    public const string IssuerType = "familytree:issuer";
    public const string AudienceType = "familytree:audience";
    public const string SubjectType = "familytree:subject";

    private static readonly string[] MarkerTypes =
        [ProviderType, IssuerType, AudienceType, SubjectType];

    /// <summary>
    /// Server-only transition called after signature/issuer/audience validation
    /// by ASP.NET Core OpenID Connect. It may not be called on raw headers,
    /// form fields or unvalidated JWT contents.
    /// </summary>
    internal static void StampAfterTokenValidation(ClaimsPrincipal principal,
        InteractiveOidc.Provider configuredProvider, string validatedIssuer,
        string? validatedSubject)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(configuredProvider);
        if (principal.Identity is not ClaimsIdentity claims || !claims.IsAuthenticated ||
            !ValidSubject(validatedSubject) || !ProviderMatches(
                configuredProvider, validatedIssuer))
            throw new UnauthorizedAccessException("OIDC identity validation failed.");

        // Do not allow IdP-controlled claims with our reserved names to be
        // confused with markers minted by this trusted server callback.
        if (principal.Claims.Any(c => MarkerTypes.Contains(c.Type, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("Unexpected identity markers.");

        claims.AddClaims(
        [
            new Claim(ProviderType, configuredProvider.Name, ClaimValueTypes.String, InternalIssuer),
            new Claim(IssuerType, validatedIssuer, ClaimValueTypes.String, InternalIssuer),
            new Claim(AudienceType, configuredProvider.ClientId, ClaimValueTypes.String, InternalIssuer),
            new Claim(SubjectType, validatedSubject!, ClaimValueTypes.String, InternalIssuer)
        ]);
    }

    /// <summary>
    /// Validate an authenticated, cryptographically signed cookie principal
    /// against CURRENT provider settings, not just a session claim saved when
    /// the provider was originally enabled. Never pass raw user-supplied claims
    /// here as proof of authentication.
    /// </summary>
    public static bool TryRead(ClaimsPrincipal? principal,
        IReadOnlyList<InteractiveOidc.Provider> currentProviders,
        out VerifiedExternalIdentity? identity)
    {
        identity = null;
        if (principal?.Identity?.IsAuthenticated != true ||
            currentProviders is null || currentProviders.Count == 0)
            return false;

        string? Value(string type)
        {
            var matches = principal.Claims.Where(c =>
                c.Type == type && c.Issuer == InternalIssuer).ToArray();
            return matches.Length == 1 ? matches[0].Value : null;
        }

        // Unexpected additional reserved claims or duplicate marker claims
        // are rejected rather than silently selecting the first.
        foreach (var type in MarkerTypes)
            if (principal.Claims.Count(c => c.Type == type) != 1)
                return false;

        var providerName = Value(ProviderType);
        var issuer = Value(IssuerType);
        var audience = Value(AudienceType);
        var subject = Value(SubjectType);
        if (providerName is null || issuer is null || audience is null ||
            !ValidSubject(subject))
            return false;

        var provider = currentProviders.SingleOrDefault(p =>
            string.Equals(p.Name, providerName, StringComparison.Ordinal));
        if (provider is null || !ProviderMatches(provider, issuer) ||
            !string.Equals(provider.ClientId, audience, StringComparison.Ordinal))
            return false;

        identity = new VerifiedExternalIdentity(provider.Name, issuer, audience, subject!);
        return true;
    }

    /// <summary>
    /// Derive an opaque stable internal principal from the verified cookie
    /// identity using a secret 256-bit runtime key. Never use a genealogy
    /// Person ID as a login identifier or return this HMAC material to callers.
    /// A key rotation requires a separately controlled migration.
    /// </summary>
    public static Guid DerivePrincipalId(VerifiedExternalIdentity identity,
        byte[] secretKey)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(secretKey);
        if (secretKey.Length != 32 || !ValidSubject(identity.Subject) ||
            string.IsNullOrWhiteSpace(identity.Provider) ||
            string.IsNullOrWhiteSpace(identity.Issuer) ||
            string.IsNullOrWhiteSpace(identity.Audience))
            throw new ArgumentException("A verified identity and 256-bit secret are required.");

        // Length-prefix fields to avoid collisions from delimiter injection.
        static void WriteField(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteField(writer, identity.Provider);
            WriteField(writer, identity.Issuer);
            WriteField(writer, identity.Audience);
            WriteField(writer, identity.Subject);
        }

        var plaintext = stream.ToArray();
        try
        {
            var hash = HMACSHA256.HashData(secretKey, plaintext);
            try
            {
                var opaque = new Guid(hash.AsSpan(0, 16));
                if (opaque == Guid.Empty)
                    throw new CryptographicException("Invalid principal identifier.");
                return opaque;
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static bool ProviderMatches(InteractiveOidc.Provider provider, string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer) ||
            string.IsNullOrWhiteSpace(provider.ClientId))
            return false;

        try
        {
            var guard = new OidcConfigurationGuard(
                [new TrustedOidcIssuer(provider.Authority, provider.ClientId)]);
            return guard.IsTrustedVerifiedIssuerAndAudience(issuer, provider.ClientId) &&
                (provider.Name, provider.Scheme, provider.Callback) switch
                {
                    ("google", "GoogleOidc", "/auth/callback/google") => true,
                    ("microsoft", "MicrosoftOidc", "/auth/callback/microsoft") => true,
                    _ => false
                };
        }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidSubject(string? subject) =>
        !string.IsNullOrWhiteSpace(subject) && subject.Length <= 256 &&
        !subject.Any(char.IsControl);
}
