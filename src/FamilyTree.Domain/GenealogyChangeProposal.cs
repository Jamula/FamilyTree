namespace FamilyTree.Domain;

/// <summary>
/// Pure state-transition rules only. No persistence, identity provider,
/// audit store, or API is exposed by this primitive.
/// </summary>
public enum ChangeReviewState { Pending, Approved, Rejected }

public sealed record GenealogyChangeProposal(
    Guid Id,
    Guid FamilyId,
    Guid SubmittedByPrincipalId,
    Guid SubjectPersonId,
    ChangeReviewState State,
    Guid? ReviewedByPrincipalId = null)
{
    public static GenealogyChangeProposal Submit(Guid familyId, Guid submitterId,
        Guid subjectPersonId)
    {
        if (familyId == Guid.Empty || submitterId == Guid.Empty ||
            subjectPersonId == Guid.Empty)
            throw new ArgumentException("Nonempty family, submitter and subject are required.");
        return new(Guid.NewGuid(), familyId, submitterId, subjectPersonId,
            ChangeReviewState.Pending);
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
        return this with
        {
            State = approve ? ChangeReviewState.Approved : ChangeReviewState.Rejected,
            ReviewedByPrincipalId = moderatorPrincipalId
        };
    }
}
