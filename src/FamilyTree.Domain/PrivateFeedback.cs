namespace FamilyTree.Domain;

/// <summary>
/// Local-only feedback intake. Escalation to a PUBLIC GitHub issue must
/// contain canned, category-only text. Never pass member free text, IDs,
/// genealogy names, contacts, screenshots or attachments to GitHub.
/// Real consent and moderator authorization must come from a trusted server.
/// </summary>
public enum FeedbackTopic { Usability, Correctness, Security, Interoperability, Accessibility, Other }
public enum FeedbackDisposition { Submitted, Triaged, Closed }

public sealed record SafePublicIssueDraft(string Title, string Body, string Label);

public sealed class PrivateFeedback
{
    private readonly string _details;
    public Guid Id { get; }
    public Guid FamilyId { get; }
    public Guid SubmitterPrincipalId { get; }
    public FeedbackTopic Topic { get; }
    public FeedbackDisposition Disposition { get; }

    private PrivateFeedback(Guid id, Guid family, Guid submitter, FeedbackTopic topic,
        FeedbackDisposition disposition, string details)
    {
        Id=id; FamilyId=family; SubmitterPrincipalId=submitter;
        Topic=topic; Disposition=disposition; _details=details;
    }

    public static PrivateFeedback Submit(Guid familyId, Guid authenticatedSubmitter,
        FeedbackTopic topic, string? privateDetails)
    {
        if (familyId == Guid.Empty || authenticatedSubmitter == Guid.Empty ||
            !Enum.IsDefined(topic) || string.IsNullOrWhiteSpace(privateDetails) ||
            privateDetails.Length > 1000)
            throw new ArgumentException("Feedback is incomplete or invalid.");
        return new(Guid.NewGuid(),familyId,authenticatedSubmitter,topic,
            FeedbackDisposition.Submitted, privateDetails.Trim());
    }

    public PrivateFeedback Triage(Guid verifiedModeratorId, Guid moderatorFamily,
        bool canModerate, bool close)
    {
        if (verifiedModeratorId == Guid.Empty ||
            verifiedModeratorId == SubmitterPrincipalId ||
            moderatorFamily != FamilyId || !canModerate ||
            Disposition == FeedbackDisposition.Closed)
            throw new UnauthorizedAccessException("Feedback review denied.");
        return new(Id,FamilyId,SubmitterPrincipalId,Topic,
            close ? FeedbackDisposition.Closed : FeedbackDisposition.Triaged,_details);
    }

    /// <summary>
    /// Entirely fixed vocabulary, independent of private content. This
    /// prepares a draft only: no GitHub issue is created or sent here.
    /// </summary>
    public SafePublicIssueDraft PreparePublicEscalation(Guid moderatorId,
        Guid moderatorFamily, bool canModerate, bool submitterConsented)
    {
        if (moderatorId == Guid.Empty || moderatorId == SubmitterPrincipalId ||
            moderatorFamily != FamilyId || !canModerate || !submitterConsented ||
            Disposition != FeedbackDisposition.Triaged)
            throw new UnauthorizedAccessException("Public escalation denied.");
        var category = Topic.ToString();
        return new SafePublicIssueDraft(
            $"[Feedback] {category} follow-up",
            $"An internal reviewer approved a {category.ToLowerInvariant()} follow-up. " +
            "All member-specific descriptions, attachments and identifiers remain private. " +
            "Reproduce with fictional test data before publication.",
            "feedback");
    }

    public string RevealToTrustedSubmitter(Guid verifiedPrincipal) =>
        verifiedPrincipal == SubmitterPrincipalId
            ? _details : throw new UnauthorizedAccessException("Feedback details denied.");
}
