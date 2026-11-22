namespace FamilyTree.Domain;

public enum RelationKind { Parent, Spouse, Partner, Sibling, Friend, Neighbor, Mentor, Other }
public enum MembershipState { Invited, PendingApproval, Active, Paused, Left }
public enum Visibility { AdministratorsOnly, BranchMembers, PersonAndRecipient }
public sealed record Person(Guid Id, string DisplayName, bool IsLiving = true);
public sealed record Relationship(Guid Id, Guid FromPersonId, Guid ToPersonId, RelationKind Kind, string? Label = null)
{
    public static Relationship Create(Guid from, Guid to, RelationKind kind, string? label = null)
    {
        if (from == to) throw new ArgumentException("A person cannot be related to themself.");
        if (kind == RelationKind.Other && string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A custom relationship needs a label.");
        return new(Guid.NewGuid(), from, to, kind, label);
    }
}
// A genealogy identity is NOT a login membership; a person may never opt in.
public sealed record FamilyMembership(Guid Id, Guid PersonId, Guid FamilyId, MembershipState State);
public sealed record TreeOptions(int GenerationsAbove = 4, int GenerationsBelow = 4,
    bool Spouses = true, bool SiblingBranches = true, bool SocialConnections = false)
{
    public TreeOptions Validate()
    {
        if (GenerationsAbove is < 0 or > 8 || GenerationsBelow is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(GenerationsAbove), "Depth must be 0–8.");
        return this;
    }
}
public sealed record Gratitude(Guid Id, Guid FromPersonId, Guid ToPersonId,
    string Message, Visibility Visibility = Visibility.PersonAndRecipient);
public interface IGenealogyExchange
{
    string Format { get; }
    Task ImportAsync(Stream input, CancellationToken cancellationToken = default);
    Task ExportAsync(Stream output, CancellationToken cancellationToken = default);
}
public interface IFamilyAuthorization
{
    Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId, CancellationToken cancellationToken = default);
    Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relationship, CancellationToken cancellationToken = default);
}
