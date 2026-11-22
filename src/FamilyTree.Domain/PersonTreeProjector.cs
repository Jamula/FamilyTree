namespace FamilyTree.Domain;

/// <summary>
/// A filtered tree projection. Callers must never combine these results with
/// unfiltered people, links, counts, or search indexes.
/// </summary>
public sealed record PersonTreeNode(Person Person, int Generation);

public sealed record PersonTreeProjection(
    Guid CenterPersonId,
    IReadOnlyList<PersonTreeNode> Nodes,
    IReadOnlyList<Relationship> Relationships);

public static class PersonTreeProjector
{
    public static async Task<PersonTreeProjection?> ProjectAsync(
        Guid viewerId,
        Guid centerPersonId,
        IEnumerable<Person> people,
        IEnumerable<Relationship> relationships,
        TreeOptions options,
        IFamilyAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorization);
        options.Validate();

        // Authorize before constructing an output graph. Unknown and invisible centers
        // both return null. API endpoints must map both to an indistinguishable result.
        var byId = people.ToDictionary(p => p.Id);
        if (!byId.ContainsKey(centerPersonId))
            return null;

        var visible = new HashSet<Guid>();
        foreach (var person in byId.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await authorization.CanSeePersonAsync(viewerId, person.Id, cancellationToken))
                visible.Add(person.Id);
        }

        if (!visible.Contains(centerPersonId))
            return null;

        var allowedEdges = new List<Relationship>();
        foreach (var relation in relationships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visible.Contains(relation.FromPersonId) &&
                visible.Contains(relation.ToPersonId) &&
                await authorization.CanSeeRelationshipAsync(viewerId, relation, cancellationToken))
                allowedEdges.Add(relation);
        }

        var included = new Dictionary<Guid, int> { [centerPersonId] = 0 };

        // Direct parent chains, bounded independently above and below center.
        void Traverse(bool ancestors, int maximumDepth)
        {
            var frontier = new HashSet<Guid> { centerPersonId };
            for (var depth = 1; depth <= maximumDepth && frontier.Count > 0; depth++)
            {
                var next = new HashSet<Guid>();
                foreach (var edge in allowedEdges)
                {
                    if (edge.Kind != RelationKind.Parent)
                        continue;

                    var anchor = ancestors ? edge.ToPersonId : edge.FromPersonId;
                    var candidate = ancestors ? edge.FromPersonId : edge.ToPersonId;
                    if (!frontier.Contains(anchor))
                        continue;

                    included.TryAdd(candidate, ancestors ? -depth : depth);
                    next.Add(candidate);
                }
                frontier = next;
            }
        }

        Traverse(ancestors: true, options.GenerationsAbove);
        Traverse(ancestors: false, options.GenerationsBelow);

        if (options.SiblingBranches)
        {
            foreach (var (personId, generation) in included.ToArray())
            {
                // Explicit sibling edges and inferred siblings of an allowed parent.
                var parentIds = allowedEdges
                    .Where(e => e.Kind == RelationKind.Parent && e.ToPersonId == personId)
                    .Select(e => e.FromPersonId).ToHashSet();

                foreach (var edge in allowedEdges)
                {
                    if (edge.Kind == RelationKind.Sibling)
                    {
                        if (edge.FromPersonId == personId)
                            included.TryAdd(edge.ToPersonId, generation);
                        if (edge.ToPersonId == personId)
                            included.TryAdd(edge.FromPersonId, generation);
                    }
                    if (edge.Kind == RelationKind.Parent && parentIds.Contains(edge.FromPersonId))
                        included.TryAdd(edge.ToPersonId, generation);
                }
            }
        }

        if (options.Spouses)
        {
            foreach (var (personId, generation) in included.ToArray())
                foreach (var edge in allowedEdges)
                    if (edge.Kind is RelationKind.Spouse or RelationKind.Partner)
                    {
                        if (edge.FromPersonId == personId)
                            included.TryAdd(edge.ToPersonId, generation);
                        if (edge.ToPersonId == personId)
                            included.TryAdd(edge.FromPersonId, generation);
                    }
        }

        if (options.SocialConnections)
        {
            // One hop only: associations do not recursively expand a genealogy tree.
            foreach (var (personId, generation) in included.ToArray())
                foreach (var edge in allowedEdges)
                    if (edge.Kind is RelationKind.Friend or RelationKind.Neighbor or RelationKind.Mentor or RelationKind.Other)
                    {
                        if (edge.FromPersonId == personId)
                            included.TryAdd(edge.ToPersonId, generation);
                        if (edge.ToPersonId == personId)
                            included.TryAdd(edge.FromPersonId, generation);
                    }
        }

        var nodes = included.Select(x => new PersonTreeNode(byId[x.Key], x.Value))
            .OrderBy(x => x.Generation).ThenBy(x => x.Person.DisplayName, StringComparer.Ordinal)
            .ToArray();

        var permittedLinks = allowedEdges
            .Where(e => included.ContainsKey(e.FromPersonId) && included.ContainsKey(e.ToPersonId))
            .Where(e => e.Kind switch
            {
                RelationKind.Spouse or RelationKind.Partner => options.Spouses,
                RelationKind.Sibling => options.SiblingBranches,
                RelationKind.Friend or RelationKind.Neighbor or RelationKind.Mentor or RelationKind.Other
                    => options.SocialConnections,
                _ => true
            }).ToArray();

        return new(centerPersonId, nodes, permittedLinks);
    }
}
