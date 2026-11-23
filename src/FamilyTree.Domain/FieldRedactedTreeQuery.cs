namespace FamilyTree.Domain;

/// <summary>
/// Return only explicitly authorized fields from a branch-constrained graph.
/// Unlike PersonTreeProjection this DTO cannot serialize living status or a
/// relationship label. No HTTP endpoints are enabled until real identity,
/// family membership and trusted grant persistence are complete.
/// </summary>
public sealed record AuthorizedTreeNode(Guid PersonId, string DisplayName, int Generation);
public sealed record AuthorizedTreeEdge(Guid RelationshipId, Guid FromPersonId,
    Guid ToPersonId, RelationKind Kind);
public sealed record AuthorizedTreeProjection(Guid CenterPersonId,
    IReadOnlyList<AuthorizedTreeNode> Nodes,
    IReadOnlyList<AuthorizedTreeEdge> Edges,
    IReadOnlyList<InferredSiblingConnection> SiblingConnections);

public static class FieldRedactedTreeQuery
{
    public static async Task<AuthorizedTreeProjection?> ProjectAsync(
        Guid viewer, Guid familyId, Guid branchId, Guid centerId,
        IEnumerable<Person> people, IEnumerable<Relationship> relationships,
        TreeOptions options, IEnumerable<BranchVisibilityGrant> trustedBranchGrants,
        FieldPrivacyPolicy trustedFieldPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trustedBranchGrants);
        ArgumentNullException.ThrowIfNull(trustedFieldPolicy);
        if (viewer == Guid.Empty || familyId == Guid.Empty || branchId == Guid.Empty ||
            trustedFieldPolicy.FamilyId != familyId)
            return null;

        var branch = new BranchScopedAuthorization(viewer, familyId, branchId,
            trustedBranchGrants);
        var both = new FieldAndBranchAuthorization(viewer, branch, trustedFieldPolicy);
        var graph = await PersonTreeProjector.ProjectAsync(
            viewer, centerId, people, relationships, options, both, cancellationToken);
        if (graph is null) return null;

        return new(graph.CenterPersonId,
            graph.Nodes.Select(n => new AuthorizedTreeNode(n.Person.Id,
                n.Person.DisplayName, n.Generation)).ToArray(),
            graph.Relationships.Select(e => new AuthorizedTreeEdge(e.Id,
                e.FromPersonId,e.ToPersonId,e.Kind)).ToArray(),
            graph.InferredSiblingConnections.ToArray());
    }

    private sealed class FieldAndBranchAuthorization(Guid principal,
        IFamilyAuthorization branch, FieldPrivacyPolicy fields) : IFamilyAuthorization
    {
        public async Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
            CancellationToken cancellationToken = default) =>
            viewerId == principal &&
            await branch.CanSeePersonAsync(viewerId, personId, cancellationToken) &&
            fields.CanRead(viewerId, personId, ProfileField.DisplayName);

        public async Task<bool> CanSeeRelationshipAsync(Guid viewerId,
            Relationship relation, CancellationToken cancellationToken = default) =>
            viewerId == principal &&
            await branch.CanSeeRelationshipAsync(viewerId, relation, cancellationToken) &&
            await CanSeePersonAsync(viewerId, relation.FromPersonId, cancellationToken) &&
            await CanSeePersonAsync(viewerId, relation.ToPersonId, cancellationToken);
    }
}
