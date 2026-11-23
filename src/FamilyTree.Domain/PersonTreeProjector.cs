namespace FamilyTree.Domain;

/// <summary>A filtered tree projection; never merge it with unfiltered records or indexes.</summary>
public sealed record PersonTreeNode(Person Person, int Generation);

/// <summary>
/// An inferred link from two visible people having an authorized common-parent
/// relationship. No parent ID is disclosed if the parent is outside the depth limit.
/// </summary>
public sealed record InferredSiblingConnection(Guid FirstPersonId, Guid SecondPersonId);

public sealed record PersonTreeProjection(
    Guid CenterPersonId,
    IReadOnlyList<PersonTreeNode> Nodes,
    IReadOnlyList<Relationship> Relationships,
    IReadOnlyList<InferredSiblingConnection> InferredSiblingConnections);

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

        // Authorization is applied before projection. Unknown and invisible centers
        // both return null; the API must return the same status and response to each.
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

        // Build authorized adjacency indexes only once, rather than scanning
        // every relationship for every person in a wide or deep graph.
        var parentsOf = new Dictionary<Guid, HashSet<Guid>>();
        var childrenOf = new Dictionary<Guid, HashSet<Guid>>();
        var explicitSiblingsOf = new Dictionary<Guid, HashSet<Guid>>();
        var spousesOf = new Dictionary<Guid, HashSet<Guid>>();
        var socialOf = new Dictionary<Guid, HashSet<Guid>>();
        var explicitSiblingPairs = new HashSet<(Guid, Guid)>();

        static void Add(Dictionary<Guid, HashSet<Guid>> map, Guid from, Guid to)
        {
            if (!map.TryGetValue(from, out var values))
                map[from] = values = new HashSet<Guid>();
            values.Add(to);
        }
        static void Both(Dictionary<Guid, HashSet<Guid>> map, Guid first, Guid second)
        {
            Add(map, first, second);
            Add(map, second, first);
        }
        static (Guid, Guid) OrderedPair(Guid a, Guid b) =>
            a.CompareTo(b) <= 0 ? (a, b) : (b, a);

        foreach (var edge in allowedEdges)
        {
            switch (edge.Kind)
            {
                case RelationKind.Parent:
                    Add(parentsOf, edge.ToPersonId, edge.FromPersonId);
                    Add(childrenOf, edge.FromPersonId, edge.ToPersonId);
                    break;
                case RelationKind.Sibling:
                    Both(explicitSiblingsOf, edge.FromPersonId, edge.ToPersonId);
                    explicitSiblingPairs.Add(OrderedPair(edge.FromPersonId, edge.ToPersonId));
                    break;
                case RelationKind.Spouse:
                case RelationKind.Partner:
                    Both(spousesOf, edge.FromPersonId, edge.ToPersonId);
                    break;
                case RelationKind.Friend:
                case RelationKind.Neighbor:
                case RelationKind.Mentor:
                case RelationKind.Other:
                    Both(socialOf, edge.FromPersonId, edge.ToPersonId);
                    break;
            }
        }

        var included = new Dictionary<Guid, int> { [centerPersonId] = 0 };

        void Traverse(Dictionary<Guid, HashSet<Guid>> adjacency, int limit, bool above)
        {
            var frontier = new HashSet<Guid> { centerPersonId };
            var visited = new HashSet<Guid> { centerPersonId };
            for (var depth = 1; depth <= limit && frontier.Count > 0; depth++)
            {
                var next = new HashSet<Guid>();
                foreach (var personId in frontier)
                {
                    if (!adjacency.TryGetValue(personId, out var neighbors))
                        continue;
                    foreach (var person in neighbors.Where(visited.Add))
                    {
                        included.TryAdd(person, above ? -depth : depth);
                        next.Add(person);
                    }
                }
                frontier = next;
            }
        }

        Traverse(parentsOf, options.GenerationsAbove, above: true);
        Traverse(childrenOf, options.GenerationsBelow, above: false);

        var inferredSiblings = new HashSet<(Guid, Guid)>();
        if (options.SiblingBranches)
        {
            foreach (var (personId, generation) in included.ToArray())
            {
                if (explicitSiblingsOf.TryGetValue(personId, out var explicitNeighbors))
                    foreach (var sibling in explicitNeighbors)
                        included.TryAdd(sibling, generation);

                if (!parentsOf.TryGetValue(personId, out var parents))
                    continue;

                foreach (var parent in parents)
                {
                    if (!childrenOf.TryGetValue(parent, out var children))
                        continue;
                    foreach (var sibling in children.Where(sibling => sibling != personId))
                    {
                        included.TryAdd(sibling, generation);
                        var pair = OrderedPair(personId, sibling);
                        if (!explicitSiblingPairs.Contains(pair))
                            inferredSiblings.Add(pair);
                    }
                }
            }
        }

        if (options.Spouses)
            foreach (var (personId, generation) in included.ToArray())
                if (spousesOf.TryGetValue(personId, out var partners))
                    foreach (var partner in partners)
                        included.TryAdd(partner, generation);

        if (options.SocialConnections)
        {
            // A social link is a one-hop association, not an ancestor chain.
            foreach (var (personId, generation) in included.ToArray())
                if (socialOf.TryGetValue(personId, out var acquaintances))
                    foreach (var acquaintance in acquaintances)
                        included.TryAdd(acquaintance, generation);
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

        var inferred = inferredSiblings
            .Where(pair => included.ContainsKey(pair.Item1) && included.ContainsKey(pair.Item2))
            .Select(pair => new InferredSiblingConnection(pair.Item1, pair.Item2))
            .OrderBy(pair => pair.FirstPersonId)
            .ThenBy(pair => pair.SecondPersonId)
            .ToArray();

        return new(centerPersonId, nodes, permittedLinks, inferred);
    }
}
