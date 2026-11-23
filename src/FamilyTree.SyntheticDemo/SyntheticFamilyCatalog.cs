using FamilyTree.Domain;

namespace FamilyTree.SyntheticDemo;

/// <summary>
/// Entirely fictional genealogy for public demonstrations. Never use as
/// authoritative data, production seed, or a copy of an actual family.
/// </summary>
public static class SyntheticFamilyCatalog
{
    public const string DataSetName = "Fictional Vale–Noor–Ash–Cross genealogy";
    public static Guid PersonId(int number) => new($"00000000-0000-0000-0000-{number:000000000000}");

    public static readonly IReadOnlyList<Person> People = BuildPeople();
    public static readonly IReadOnlyList<Relationship> Relationships = BuildRelationships();

    private static Person[] BuildPeople()
    {
        string[] names =
        [
            "Rowan Vale", "Elara Vale", "Omar Noor", "Mina Noor",
            "Silas Ash", "Nia Ash", "Iris Vale", "Leo Vale",
            "Anya Noor", "Dev Noor", "Petra Ash", "Hugo Ash",
            "Mira Vale", "Kian Vale", "Samira Noor", "Ezra Noor",
            "Noor Ash", "Ravi Ash", "Ada Vale", "Theo Vale",
            "Luna Noor", "Arin Noor", "Sia Ash", "Niko Ash",
            "Jo Vale", "Tessa Vale", "Eli Grey", "Isla Grey",
            "Sol Cross", "Kit Grey", "Imani Cross", "Casey Cross"
        ];
        return names.Select((name, index) => new Person(PersonId(index + 1), name))
            .ToArray();
    }

    private static Relationship[] BuildRelationships()
    {
        var edges = new List<Relationship>();
        void Link(int from, int to, RelationKind kind, string? label = null)
        {
            edges.Add(new Relationship(PersonId(1001 + edges.Count),
                PersonId(from), PersonId(to), kind, label));
        }

        // Three founding branches.
        Link(1, 2, RelationKind.Spouse);
        Link(3, 4, RelationKind.Spouse);
        Link(5, 6, RelationKind.Spouse);
        foreach (var child in new[] { 7, 8 })
        {
            Link(1, child, RelationKind.Parent);
            Link(2, child, RelationKind.Parent);
        }
        foreach (var child in new[] { 9, 10 })
        {
            Link(3, child, RelationKind.Parent);
            Link(4, child, RelationKind.Parent);
        }
        foreach (var child in new[] { 11, 12 })
        {
            Link(5, child, RelationKind.Parent);
            Link(6, child, RelationKind.Parent);
        }

        // G2 joins three branches.
        Link(7, 9, RelationKind.Spouse);
        Link(8, 11, RelationKind.Partner);
        Link(10, 12, RelationKind.Spouse);
        foreach (var child in new[] { 13, 14 })
        {
            Link(7, child, RelationKind.Parent);
            Link(9, child, RelationKind.Parent);
        }
        foreach (var child in new[] { 15, 16 })
        {
            Link(8, child, RelationKind.Parent);
            Link(11, child, RelationKind.Parent);
        }
        foreach (var child in new[] { 17, 18 })
        {
            Link(10, child, RelationKind.Parent);
            Link(12, child, RelationKind.Parent);
        }

        // G3 partners, blended family, step-relationship and different parent links.
        Link(13, 17, RelationKind.Spouse);
        Link(13, 31, RelationKind.Partner);
        Link(31, 32, RelationKind.Parent);
        Link(13, 32, RelationKind.Other, "step-parent");
        Link(14, 15, RelationKind.Spouse);
        Link(16, 18, RelationKind.Partner);
        foreach (var child in new[] { 19, 20 })
        {
            Link(13, child, RelationKind.Parent);
            Link(17, child, RelationKind.Parent);
        }
        foreach (var child in new[] { 21, 22 })
        {
            Link(14, child, RelationKind.Parent);
            Link(15, child, RelationKind.Parent);
        }
        foreach (var child in new[] { 23, 24 })
        {
            Link(16, child, RelationKind.Parent);
            Link(18, child, RelationKind.Parent);
        }

        // G4–G5, partner links and unrelated people joining the graph.
        Link(19, 27, RelationKind.Partner);
        Link(21, 28, RelationKind.Spouse);
        Link(23, 29, RelationKind.Partner);
        Link(19, 25, RelationKind.Parent);
        Link(27, 25, RelationKind.Parent);
        Link(21, 26, RelationKind.Parent);
        Link(28, 26, RelationKind.Parent);

        // Distinct social connections and multiple independent link types.
        Link(14, 18, RelationKind.Friend);
        Link(14, 18, RelationKind.Neighbor);
        Link(10, 26, RelationKind.Mentor);
        Link(11, 17, RelationKind.Neighbor);
        Link(13, 22, RelationKind.Friend);

        // #30 Kit Grey intentionally remains disconnected.
        return edges.ToArray();
    }
}
