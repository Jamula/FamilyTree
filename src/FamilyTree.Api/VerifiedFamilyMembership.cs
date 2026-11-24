using FamilyTree.Persistence;

namespace FamilyTree.Api;

/// <summary>
/// Local integration seam for protected OIDC cookies and durable membership.
/// It does not expose HTTP routes or grant access to private graph records.
/// Each private data operation must enforce membership and graph grants in
/// its own transaction; a prior membership check is not an authorization lease.
/// </summary>
public sealed class VerifiedFamilyMembership(
    SqlitePrincipalRegistry registry,
    SqliteFamilyStore families,
    IReadOnlyList<InteractiveOidc.Provider> providers)
{
    private readonly SqlitePrincipalRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly SqliteFamilyStore _families = families ?? throw new ArgumentNullException(nameof(families));
    private readonly InteractiveOidc.Provider[] _providers =
        (providers ?? throw new ArgumentNullException(nameof(providers))).ToArray();

    public async Task<Guid?> RegisterAsync(HttpContext context)
    {
        var binding = await VerifiedOidcSession.ReadBindingAsync(context, _providers);
        return binding is { } identity
            ? _registry.EnrollVerifiedSubject(identity.Provider, identity.Issuer, identity.Subject)
            : null;
    }

    public async Task<bool> IsActiveMemberAsync(HttpContext context, Guid familyId)
    {
        var principal = await FindPrincipalAsync(context);
        return principal is { } id && familyId != Guid.Empty && _families.IsActiveMember(familyId, id);
    }

    public Task<Guid?> ResolvePrincipalIdAsync(HttpContext context) => FindPrincipalAsync(context);

    public async Task<Guid> IssueInvitationAsync(HttpContext context, Guid familyId,
        Guid intendedPrincipalId, DateTimeOffset expiry)
    {
        var principal = await RequirePrincipalAsync(context);
        return _families.IssueInvitation(familyId, principal, intendedPrincipalId, expiry,
            DateTimeOffset.UtcNow);
    }

    public async Task AcceptInvitationAsync(HttpContext context, Guid familyId, Guid invitationId)
    {
        var principal = await RequirePrincipalAsync(context);
        _families.AcceptInvitation(familyId, principal, invitationId, DateTimeOffset.UtcNow);
    }

    public async Task ApproveMemberAsync(HttpContext context, Guid familyId, Guid memberId)
    {
        var principal = await RequirePrincipalAsync(context);
        _families.ApproveMember(familyId, principal, memberId);
    }

    public async Task RevokeMemberAsync(HttpContext context, Guid familyId, Guid memberId)
    {
        var principal = await RequirePrincipalAsync(context);
        _families.RevokeMember(familyId, principal, memberId);
    }

    private async Task<Guid?> FindPrincipalAsync(HttpContext context)
    {
        var binding = await VerifiedOidcSession.ReadBindingAsync(context, _providers);
        return binding is { } identity
            ? _registry.FindVerifiedSubject(identity.Provider, identity.Issuer, identity.Subject)
            : null;
    }

    private async Task<Guid> RequirePrincipalAsync(HttpContext context) =>
        await FindPrincipalAsync(context) ?? throw new UnauthorizedAccessException("Verified registered identity required.");
}
