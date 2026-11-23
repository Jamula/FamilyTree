namespace FamilyTree.Domain;

/// <summary>
/// Validates static IdP configuration only, NOT JWTs. A future OIDC/JWT
/// handler must independently verify signature, trusted discovery keys,
/// issuer, audience, nonce where applicable, time bounds and auth flow.
/// Never use this policy alone to authenticate a caller.
/// </summary>
public sealed record TrustedOidcIssuer(string Issuer, string Audience);

public sealed class OidcConfigurationGuard
{
    private readonly TrustedOidcIssuer[] _issuers;

    public OidcConfigurationGuard(IEnumerable<TrustedOidcIssuer> trustedIssuers)
    {
        ArgumentNullException.ThrowIfNull(trustedIssuers);
        _issuers = trustedIssuers.ToArray();
        if (_issuers.Length == 0 || _issuers.Length > 8 ||
            _issuers.Any(x => !IsSafeIssuer(x.Issuer) ||
                string.IsNullOrWhiteSpace(x.Audience) ||
                x.Audience.Length > 256 ||
                x.Audience.Any(char.IsWhiteSpace)) ||
            _issuers.GroupBy(x => (x.Issuer, x.Audience)).Any(g => g.Count() > 1))
            throw new ArgumentException("A nonempty, exact OIDC issuer and audience allowlist is required.");
    }

    /// <summary>
    /// Post-verification defense-in-depth only. Never pass token strings
    /// or unverified claims here to make an authentication decision.
    /// </summary>
    public bool IsTrustedVerifiedIssuerAndAudience(string? verifiedIssuer,
        string? verifiedAudience) =>
        _issuers.Any(x => string.Equals(x.Issuer, verifiedIssuer,
                StringComparison.Ordinal) &&
            string.Equals(x.Audience, verifiedAudience, StringComparison.Ordinal));

    private static bool IsSafeIssuer(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 256 ||
            !Uri.TryCreate(issuer, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return false;

        // Explicit provider allowlisting avoids trusting attacker-controlled
        // discovery hosts, wildcard tenants and loopback issuers.
        if (string.Equals(url.Host, "accounts.google.com", StringComparison.Ordinal) &&
            (url.AbsolutePath == "/" || url.AbsolutePath == ""))
            return true;

        if (string.Equals(url.Host, "login.microsoftonline.com", StringComparison.Ordinal))
        {
            var parts = url.AbsolutePath.Trim('/').Split('/');
            return parts.Length == 2 &&
                Guid.TryParse(parts[0], out var tenantId) &&
                tenantId != Guid.Empty &&
                string.Equals(parts[1], "v2.0", StringComparison.Ordinal);
        }
        return false;
    }
}
