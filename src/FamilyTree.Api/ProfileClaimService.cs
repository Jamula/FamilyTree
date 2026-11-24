using FamilyTree.Domain;
using FamilyTree.Persistence;

namespace FamilyTree.Api;

/// <summary>
/// Binds claim operations to the verified cookie identity. This is an
/// integration seam only; it does not expose a private HTTP route.
/// </summary>
public sealed class ProfileClaimService(
    VerifiedFamilyMembership membership,
    SqliteFamilyStore families)
{
    private readonly VerifiedFamilyMembership _membership =
        membership ?? throw new ArgumentNullException(nameof(membership));
    private readonly SqliteFamilyStore _families =
        families ?? throw new ArgumentNullException(nameof(families));

    public async Task<SqliteFamilyStore.StoredProfileClaim> ProposeAsync(
        HttpContext context, Guid familyId, Guid branchId, Guid personId)
    {
        var principal = await RequirePrincipalAsync(context);
        return _families.ProposeProfileClaim(familyId, principal, branchId, personId);
    }

    public async Task<SqliteFamilyStore.StoredProfileClaim> ReviewAsync(
        HttpContext context, Guid familyId, Guid claimId, bool approve)
    {
        var reviewer = await RequirePrincipalAsync(context);
        return _families.ReviewProfileClaim(familyId, reviewer, claimId, approve);
    }

    public async Task<SqliteFamilyStore.StoredProfileClaim> WithdrawAsync(
        HttpContext context, Guid familyId, Guid claimId)
    {
        var principal = await RequirePrincipalAsync(context);
        return _families.WithdrawProfileClaim(familyId, principal, claimId);
    }

    public async Task<IReadOnlyList<SqliteFamilyStore.StoredProfileClaim>> ListPendingAsync(
        HttpContext context, Guid familyId)
    {
        var administrator = await RequirePrincipalAsync(context);
        return _families.ListPendingProfileClaims(familyId, administrator);
    }

    public async Task<IReadOnlyList<SqliteFamilyStore.ProfileClaimAuditEntry>> ReadAuditAsync(
        HttpContext context, Guid familyId, Guid claimId)
    {
        var administrator = await RequirePrincipalAsync(context);
        return _families.ReadProfileClaimAudit(familyId, administrator, claimId);
    }

    private async Task<Guid> RequirePrincipalAsync(HttpContext context) =>
        await _membership.ResolvePrincipalIdAsync(context) ??
        throw new UnauthorizedAccessException("Verified registered identity required.");
}
