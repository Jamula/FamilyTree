using FamilyTree.Domain;

namespace FamilyTree.Persistence;

/// <summary>
/// Local read adapter for verified server principals. This is not an HTTP
/// authentication boundary and is not registered with private routes.
/// </summary>
public sealed class SqlitePersonReadService(SqliteTreeReadSource source)
{
    private readonly SqliteTreeReadSource _source =
        source ?? throw new ArgumentNullException(nameof(source));

    public async Task<AuthorizedPersonSummary?> GetAsync(Guid viewer, Guid family,
        Guid branch, Guid person, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (person == Guid.Empty) return null;
        var snapshot = await _source.LoadAsync(viewer, family, branch, cancellationToken);
        if (snapshot is null) return null;
        var authorization = Authorization(snapshot);
        return await AuthorizedFamilyQuery.GetAsync(viewer, person, snapshot.People,
            authorization, snapshot.FieldGrants, cancellationToken);
    }

    public async Task<IReadOnlyList<AuthorizedPersonSummary>> SearchAsync(Guid viewer,
        Guid family, Guid branch, string? text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80 || text.Trim().Length < 2)
            return Array.Empty<AuthorizedPersonSummary>();
        var snapshot = await _source.LoadAsync(viewer, family, branch, cancellationToken);
        if (snapshot is null) return Array.Empty<AuthorizedPersonSummary>();
        var authorization = Authorization(snapshot);
        return await AuthorizedFamilyQuery.SearchAsync(viewer, snapshot.People, text,
            authorization, snapshot.FieldGrants, cancellationToken);
    }

    private static MembershipScopedAuthorization Authorization(TrustedTreeReadSnapshot snapshot)
    {
        if (snapshot.People.Count > 10_000)
            throw new InvalidOperationException("Trusted tree snapshot is invalid.");
        var grants = snapshot.BranchGrants
            .Where(grant => grant.PrincipalId == snapshot.ViewerId &&
                grant.FamilyId == snapshot.FamilyId && grant.BranchId == snapshot.BranchId &&
                grant.State == MembershipState.Active)
            .Select(grant => new ScopedFamilyGrant(grant.PrincipalId, grant.FamilyId,
                grant.State, grant.VisiblePeople, grant.VisibleRelationships));
        return new MembershipScopedAuthorization(snapshot.FamilyId, grants);
    }
}
