namespace FamilyTree.Domain;

/// <summary>
/// Explicit provenance and chronology for one independently identified link.
/// Never infer social vs family status from someone sharing a surname.
/// This is an immutable draft model, not a publication permission.
/// </summary>
public enum AssociationCategory { Genealogical, Social }

public sealed class RelationshipProvenance
{
    public Guid FamilyId { get; }
    public Guid BranchId { get; }
    public Relationship Link { get; }
    public AssociationCategory Category { get; }
    public DateOnly? StartDate { get; }
    public DateOnly? EndDate { get; }
    public string Citation { get; }

    private RelationshipProvenance(Guid familyId, Guid branchId, Relationship link,
        AssociationCategory category, DateOnly? start, DateOnly? end, string citation)
    {
        FamilyId=familyId;
        BranchId=branchId;
        Link=link;
        Category=category;
        StartDate=start;
        EndDate=end;
        Citation=citation;
    }

    public static RelationshipProvenance Create(Guid familyId, Guid branchId,
        Relationship link, AssociationCategory category, string citation,
        DateOnly? start=null, DateOnly? end=null)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (familyId == Guid.Empty || branchId == Guid.Empty ||
            link.Id == Guid.Empty || link.FromPersonId == Guid.Empty ||
            link.ToPersonId == Guid.Empty || link.FromPersonId == link.ToPersonId ||
            !Enum.IsDefined(link.Kind) || !Enum.IsDefined(category) ||
            string.IsNullOrWhiteSpace(citation) || citation.Length > 500 ||
            citation.IndexOfAny(['\r','\n']) >= 0 || (start is not null &&
                end is not null && end.Value < start.Value))
            throw new ArgumentException("Invalid relationship provenance.");
        if (link.Kind is RelationKind.Parent or RelationKind.Spouse or
            RelationKind.Partner or RelationKind.Sibling)
        {
            if (category != AssociationCategory.Genealogical)
                throw new ArgumentException("A family relationship cannot be social-only.");
        }
        else if (link.Kind is RelationKind.Friend or RelationKind.Neighbor or RelationKind.Mentor)
        {
            if (category != AssociationCategory.Social)
                throw new ArgumentException("A social association cannot become an ancestor.");
        }
        else if (link.Kind == RelationKind.Other &&
            string.IsNullOrWhiteSpace(link.Label))
            throw new ArgumentException("A custom relationship needs a label.");

        return new(familyId, branchId, link, category, start, end, citation.Trim());
    }
}
