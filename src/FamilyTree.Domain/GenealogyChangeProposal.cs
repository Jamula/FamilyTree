namespace FamilyTree.Domain;

/// <summary>
/// Immutable proposal state machine. Construction is restricted to Submit
/// and Review; no public init setters or with-clones can bypass moderation.
/// Identity and moderator authority must be verified by the server.
/// </summary>
public enum ChangeReviewState { Pending, Approved, Rejected }

public sealed class GenealogyChangeProposal
{
    public Guid Id { get; }
    public Guid FamilyId { get; }
    public Guid SubmittedByPrincipalId { get; }
    public Guid SubjectPersonId { get; }
    public ChangeReviewState State { get; }
    public Guid? ReviewedByPrincipalId { get; }

    private GenealogyChangeProposal(Guid id, Guid familyId, Guid submitter,
        Guid subject, ChangeReviewState state, Guid? reviewer)
    {
        Id = id; FamilyId = familyId; SubmittedByPrincipalId = submitter;
        SubjectPersonId = subject; State = state; ReviewedByPrincipalId = reviewer;
    }

    public static GenealogyChangeProposal Submit(Guid familyId, Guid submitterId,
        Guid subjectPersonId)
    {
        if (familyId == Guid.Empty || submitterId == Guid.Empty ||
            subjectPersonId == Guid.Empty)
            throw new ArgumentException("Nonempty family, submitter and subject are required.");
        return new(Guid.NewGuid(), familyId, submitterId, subjectPersonId,
            ChangeReviewState.Pending, null);
    }

    public GenealogyChangeProposal Review(Guid moderatorPrincipalId,
        Guid moderatorFamilyId, bool canModerate, bool approve)
    {
        if (State != ChangeReviewState.Pending)
            throw new InvalidOperationException("A reviewed change cannot be reviewed again.");
        if (moderatorPrincipalId == Guid.Empty ||
            moderatorPrincipalId == SubmittedByPrincipalId ||
            moderatorFamilyId != FamilyId || !canModerate)
            throw new UnauthorizedAccessException("Review is not authorized.");
        return new(Id, FamilyId, SubmittedByPrincipalId, SubjectPersonId,
            approve ? ChangeReviewState.Approved : ChangeReviewState.Rejected,
            moderatorPrincipalId);
    }
}
