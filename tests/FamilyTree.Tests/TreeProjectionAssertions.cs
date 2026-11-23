using FamilyTree.Domain;

internal static class TreeProjectionAssertions
{
    public static async Task RunAsync()
    {
        var viewer = Guid.NewGuid();
        var center = new Person(Guid.NewGuid(), "Center");
        var father = new Person(Guid.NewGuid(), "Father");
        var mother = new Person(Guid.NewGuid(), "Mother");
        var sibling = new Person(Guid.NewGuid(), "Sibling");
        var child = new Person(Guid.NewGuid(), "Child");
        var spouse = new Person(Guid.NewGuid(), "Spouse");
        var friend = new Person(Guid.NewGuid(), "Friend");
        var secret = new Person(Guid.NewGuid(), "Hidden");
        var people = new[] { center, father, mother, sibling, child, spouse, friend, secret };
        var links = new[]
        {
            Relationship.Create(father.Id, center.Id, RelationKind.Parent),
            Relationship.Create(mother.Id, center.Id, RelationKind.Parent),
            Relationship.Create(father.Id, sibling.Id, RelationKind.Parent),
            Relationship.Create(center.Id, child.Id, RelationKind.Parent),
            Relationship.Create(center.Id, spouse.Id, RelationKind.Spouse),
            Relationship.Create(center.Id, friend.Id, RelationKind.Friend),
            Relationship.Create(center.Id, friend.Id, RelationKind.Neighbor),
            Relationship.Create(secret.Id, father.Id, RelationKind.Parent),
            Relationship.Create(center.Id, secret.Id, RelationKind.Friend)
        };
        var auth = new SyntheticAuthorization(people.Where(p => p.Id != secret.Id).Select(p => p.Id));
        var defaultTree = await PersonTreeProjector.ProjectAsync(
            viewer, center.Id, people, links, new TreeOptions(), auth);
        var verifiedTree = defaultTree ?? throw new InvalidOperationException("authorized center must be visible");
        var defaultIds = verifiedTree.Nodes.Select(x => x.Person.Id).ToHashSet();
        Ensure(defaultIds.SetEquals(new[] {center.Id, father.Id, mother.Id, child.Id, sibling.Id, spouse.Id }),
            "default tree must show direct family, sibling, spouse, but no social contacts");
        Ensure(verifiedTree.Relationships.All(e => defaultIds.Contains(e.FromPersonId) && defaultIds.Contains(e.ToPersonId)),
            "tree links must have authorized endpoints");
        Ensure(!defaultIds.Contains(secret.Id), "hidden people must never appear in nodes");
        Ensure(verifiedTree.Relationships.All(e => e.FromPersonId != secret.Id && e.ToPersonId != secret.Id),
            "hidden people must never leak through edges");
        Ensure(verifiedTree.Nodes.Single(x => x.Person.Id == father.Id).Generation == -1,
            "ancestor must be positioned above center");
        Ensure(verifiedTree.Nodes.Single(x => x.Person.Id == child.Id).Generation == 1,
            "descendant must be positioned below center");
        Ensure(verifiedTree.Nodes.Single(x => x.Person.Id == sibling.Id).Generation == 0,
            "sibling must stay in same generation");

        var zeroDepthSiblings = await PersonTreeProjector.ProjectAsync(
            viewer, center.Id, people, links, new TreeOptions(0, 0, false, true, false), auth);
        var verifiedSiblings = zeroDepthSiblings ?? throw new InvalidOperationException("authorized zero-depth tree must exist");
        Ensure(verifiedSiblings.Nodes.Select(x => x.Person.Id)
            .ToHashSet().SetEquals(new[] {center.Id, sibling.Id}),
            "zero-depth sibling view must exclude the out-of-depth parent");
        Ensure(verifiedSiblings.InferredSiblingConnections.Any(e =>
            (e.FirstPersonId == center.Id && e.SecondPersonId == sibling.Id)
            || (e.FirstPersonId == sibling.Id && e.SecondPersonId == center.Id)),
            "inferred sibling link must remain visible without leaking the parent");

        var social = await PersonTreeProjector.ProjectAsync(
            viewer, center.Id, people, links, new TreeOptions(0, 0, false, false, true), auth);
        Ensure(social is not null && social.Nodes.Select(x => x.Person.Id)
            .ToHashSet().SetEquals(new[] {center.Id, friend.Id}),
            "social-only tree must respect all relation toggles and zero depth");
        Ensure(social!.Relationships.Count(e => e.ToPersonId == friend.Id) == 2,
            "distinct friend and neighbor links must both be preserved");
        Ensure(await PersonTreeProjector.ProjectAsync(viewer, secret.Id, people, links, new(), auth) is null,
            "unknown and hidden centers should both return no graph");
        Ensure(await PersonTreeProjector.ProjectAsync(viewer, Guid.NewGuid(), people, links, new(), auth) is null,
            "unknown centers should return no graph");
        auth.DeniedRelationships.Add(links[4].Id);
        var withoutMarriage = await PersonTreeProjector.ProjectAsync(
            viewer, center.Id, people, links, new(), auth);
        Ensure(withoutMarriage is not null && withoutMarriage.Nodes.All(x => x.Person.Id != spouse.Id),
            "relationship-level denial must hide an otherwise authorized person from traversal");
        Console.WriteLine("PASS 11 person-tree authorization and traversal assertions");
    }

    private static void Ensure(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private sealed class SyntheticAuthorization(IEnumerable<Guid> personIds) : IFamilyAuthorization
    {
        private readonly HashSet<Guid> _persons = personIds.ToHashSet();
        public HashSet<Guid> DeniedRelationships { get; } = new();
        public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_persons.Contains(personId));
        public Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relationship,
            CancellationToken cancellationToken = default)
            => Task.FromResult(!DeniedRelationships.Contains(relationship.Id));
    }
}
