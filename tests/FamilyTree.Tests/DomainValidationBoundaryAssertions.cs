using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;

internal static class DomainValidationBoundaryAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-synthetic-snapshot-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Join(directory, "snapshot.bin");
        var first = new Person(Guid.NewGuid(), "Fictional Validation One");
        var second = new Person(Guid.NewGuid(), "Fictional Validation Two");
        var family = Guid.NewGuid();
        var valid = new LocalFamilySnapshot(family, 1, [first, second], []);
        var rejected = 0;
        try
        {
            Expect<ArgumentException>(() =>
                new EncryptedLocalSnapshotStore("relative.snapshot", key),
                "relative snapshot path rejected");
            using var store = new EncryptedLocalSnapshotStore(path, key);
            void Invalid(LocalFamilySnapshot snapshot)
            {
                Expect<InvalidDataException>(() =>
                    store.SaveAsync(snapshot).GetAwaiter().GetResult(),
                    "invalid synthetic snapshot must be rejected");
                rejected++;
            }

            Invalid(valid with { FamilyId = Guid.Empty });
            Invalid(valid with { Revision = -1 });
            Invalid(valid with { People = null! });
            Invalid(valid with { Relationships = null! });
            Invalid(valid with { People = [null!] });
            Invalid(valid with { People = [new Person(Guid.Empty, "Fictional")] });
            Invalid(valid with { People = [new Person(Guid.NewGuid(), " ")] });
            Invalid(valid with { People = [new Person(Guid.NewGuid(), new string('x', 201))] });
            Invalid(valid with { Relationships = [null!] });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.Empty, first.Id, second.Id,
                    RelationKind.Parent)]
            });
            var repeated = new Relationship(Guid.NewGuid(), first.Id, second.Id,
                RelationKind.Parent);
            Invalid(valid with { Relationships = [repeated, repeated] });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.NewGuid(), first.Id, Guid.NewGuid(),
                    RelationKind.Parent)]
            });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.NewGuid(), first.Id, first.Id,
                    RelationKind.Parent)]
            });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.NewGuid(), first.Id, second.Id,
                    (RelationKind)999)]
            });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.NewGuid(), first.Id, second.Id,
                    RelationKind.Other)]
            });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.NewGuid(), first.Id, second.Id,
                    RelationKind.Friend, new string('x', 201))]
            });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.NewGuid(), first.Id, second.Id,
                    RelationKind.Friend, "synthetic\rlabel")]
            });
            Invalid(valid with
            {
                Relationships = [new Relationship(Guid.NewGuid(), first.Id, second.Id,
                    RelationKind.Friend, "synthetic\nlabel")]
            });
            Invalid(valid with
            {
                People = Enumerable.Range(0, 10001)
                    .Select(_ => new Person(Guid.NewGuid(), "Fictional Count Boundary"))
                    .ToArray()
            });
            Invalid(valid with
            {
                Relationships = Enumerable.Range(0, 20001)
                    .Select(_ => new Relationship(Guid.NewGuid(), first.Id, second.Id,
                        RelationKind.Parent))
                    .ToArray()
            });
            Check(rejected == 20, "all synthetic snapshot validation boundaries rejected");
            var oversized = valid with
            {
                People = Enumerable.Range(0, 10000)
                    .Select(_ => new Person(Guid.NewGuid(), new string('x', 200)))
                    .ToArray()
            };
            Expect<InvalidOperationException>(() =>
                store.SaveAsync(oversized).GetAwaiter().GetResult(),
                "serialized snapshot beyond the byte ceiling rejected");
            File.WriteAllBytes(path, new byte[28]);
            Expect<InvalidDataException>(() =>
                store.LoadAsync().GetAwaiter().GetResult(),
                "undersized encrypted envelope rejected");
            File.WriteAllBytes(path, new byte[32]);
            Expect<InvalidDataException>(() =>
                store.LoadAsync().GetAwaiter().GetResult(),
                "invalid snapshot magic rejected");
            File.WriteAllBytes(path, new byte[2_000_033]);
            Expect<InvalidDataException>(() =>
                store.LoadAsync().GetAwaiter().GetResult(),
                "oversized encrypted envelope rejected");
            Console.WriteLine($"PASS {rejected} synthetic snapshot validation outcomes");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(directory, recursive: true);
        }

        var principal = Guid.NewGuid();
        var visibleFamily = Guid.NewGuid();
        var visiblePerson = new Person(Guid.NewGuid(), "Fictional Visible");
        var stalePersonId = Guid.NewGuid();
        var membership = new MembershipScopedAuthorization(visibleFamily,
            [new ScopedFamilyGrant(principal, visibleFamily, MembershipState.Active,
                new HashSet<Guid> { visiblePerson.Id, stalePersonId }, new HashSet<Guid>())]);
        var fieldPolicy = new FieldPrivacyPolicy(visibleFamily,
            [new FamilyFieldGrant(principal, visibleFamily, stalePersonId,
                MembershipState.Active, new HashSet<ProfileField>
                    { ProfileField.DisplayName })]);
        Check(AuthorizedFamilyQuery.GetAsync(principal, stalePersonId,
                [visiblePerson], membership, fieldPolicy).GetAwaiter().GetResult() is null,
            "stale authorized person identifier returns no summary");
        Check(!fieldPolicy.CanRead(Guid.Empty, stalePersonId, ProfileField.DisplayName) &&
            !fieldPolicy.CanRead(principal, Guid.Empty, ProfileField.DisplayName) &&
            !fieldPolicy.CanRead(principal, stalePersonId, ProfileField.BirthDate),
            "empty identities and ungranted private fields remain denied");
        Expect<ArgumentNullException>(() =>
            new FieldPrivacyPolicy(visibleFamily, null!),
            "missing trusted field grants rejected");
        Expect<ArgumentNullException>(() =>
            new BranchScopedAuthorization(principal, visibleFamily, Guid.NewGuid(), null!),
            "missing trusted branch grants rejected");
        Expect<ArgumentNullException>(() =>
            membership.CanSeeRelationshipAsync(principal, null!)
                .GetAwaiter().GetResult(),
            "missing relationship denied by membership authorization");

        var center = new Person(Guid.NewGuid(), "Fictional Explicit Sibling Center");
        var sibling = new Person(Guid.NewGuid(), "Fictional Explicit Sibling");
        var siblingEdge = Relationship.Create(center.Id, sibling.Id, RelationKind.Sibling);
        var authorized = new BoundaryAuthorization([center.Id, sibling.Id], [siblingEdge.Id]);
        var tree = PersonTreeProjector.ProjectAsync(principal, center.Id, [center, sibling],
            [siblingEdge], new TreeOptions(0, 0, false, true, false), authorized)
            .GetAwaiter().GetResult();
        Check(tree is not null && tree.Nodes.Count == 2 &&
            tree.Relationships.Contains(siblingEdge),
            "explicit sibling relation includes the authorized same-generation person");
        Expect<ArgumentException>(() =>
            GenealogyChangeProposal.Submit(Guid.Empty, principal, center.Id),
            "empty proposal family rejected");
        Expect<ArgumentException>(() =>
            GenealogyChangeProposal.Submit(visibleFamily, Guid.Empty, center.Id),
            "empty proposal submitter rejected");
        Expect<ArgumentException>(() =>
            GenealogyChangeProposal.Submit(visibleFamily, principal, Guid.Empty),
            "empty proposal subject rejected");
        var proposal = GenealogyChangeProposal.Submit(
            visibleFamily, principal, center.Id);
        Expect<UnauthorizedAccessException>(() =>
            proposal.Review(Guid.Empty, visibleFamily, true, true),
            "empty moderator rejected");
        Expect<ArgumentNullException>(() => PersonTreeProjector.ProjectAsync(principal,
            center.Id, null!, [siblingEdge], new TreeOptions(), authorized)
            .GetAwaiter().GetResult(), "missing people collection rejected");
        Expect<ArgumentNullException>(() => PersonTreeProjector.ProjectAsync(principal,
            center.Id, [center], null!, new TreeOptions(), authorized)
            .GetAwaiter().GetResult(), "missing relationship collection rejected");
        Expect<ArgumentNullException>(() => PersonTreeProjector.ProjectAsync(principal,
            center.Id, [center], [], null!, authorized)
            .GetAwaiter().GetResult(), "missing tree options rejected");
        Expect<ArgumentNullException>(() => PersonTreeProjector.ProjectAsync(principal,
            center.Id, [center], [], new TreeOptions(), null!)
            .GetAwaiter().GetResult(), "missing tree authorization rejected");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static void Expect<T>(Action action, string reason) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(reason);
    }

    private sealed class BoundaryAuthorization : IFamilyAuthorization
    {
        private readonly HashSet<Guid> _people;
        private readonly HashSet<Guid> _relationships;

        public BoundaryAuthorization(IEnumerable<Guid> people,
            IEnumerable<Guid> relationships)
        {
            _people = people.ToHashSet();
            _relationships = relationships.ToHashSet();
        }

        public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_people.Contains(personId));

        public Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relationship,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_relationships.Contains(relationship.Id));
    }
}
