namespace FamilyTree.Domain;

/// <summary>
/// A snapshot produced ONLY by trusted server storage after session validation.
/// Its contents must not be populated from request parameters, headers or JSON.
/// This service is not yet wired to private HTTP routes.
/// </summary>
public sealed record TrustedTreeReadSnapshot(
    Guid ViewerId, Guid FamilyId, Guid BranchId, bool ActiveMembership,
    IReadOnlyList<Person> People, IReadOnlyList<Relationship> Relationships,
    IReadOnlyList<BranchVisibilityGrant> BranchGrants,
    FieldPrivacyPolicy FieldGrants);

public interface ITrustedTreeReadSource
{
    Task<TrustedTreeReadSnapshot?> LoadAsync(
        Guid viewer, Guid family, Guid branch, CancellationToken cancellationToken);
}

/// <summary>
/// Executes a field-redacted, branch-scoped tree read from a trusted server
/// snapshot. A forbidden, revoked or unknown center yields the same null result.
/// No data from another family/branch or disallowed fields is serialized.
/// </summary>
public sealed class PrivateTreeReadService
{
    private readonly ITrustedTreeReadSource _source;
    private const int MaxPeople = 10_000;
    private const int MaxRelationships = 20_000;

    public PrivateTreeReadService(ITrustedTreeReadSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async Task<AuthorizedTreeProjection?> GetAsync(
        Guid verifiedViewer, Guid family, Guid branch, Guid center,
        TreeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (verifiedViewer == Guid.Empty || family == Guid.Empty ||
            branch == Guid.Empty || center == Guid.Empty)
            return null;

        var snapshot = await _source.LoadAsync(
            verifiedViewer, family, branch, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null || !snapshot.ActiveMembership ||
            snapshot.ViewerId != verifiedViewer || snapshot.FamilyId != family ||
            snapshot.BranchId != branch)
            return null;
        if (snapshot.People is null || snapshot.Relationships is null ||
            snapshot.BranchGrants is null || snapshot.FieldGrants is null ||
            snapshot.FieldGrants.FamilyId != family ||
            snapshot.People.Count > MaxPeople ||
            snapshot.Relationships.Count > MaxRelationships ||
            snapshot.People.Any(person => person is null || person.Id == Guid.Empty) ||
            snapshot.People.Select(person => person.Id).Distinct().Count() != snapshot.People.Count ||
            snapshot.Relationships.Any(edge => edge is null || edge.Id == Guid.Empty))
            throw new InvalidOperationException("Trusted tree snapshot is invalid.");

        return await FieldRedactedTreeQuery.ProjectAsync(
            verifiedViewer, family, branch, center,
            snapshot.People, snapshot.Relationships, options,
            snapshot.BranchGrants, snapshot.FieldGrants, cancellationToken);
    }
}
