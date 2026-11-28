namespace FamilyTree.Domain;

public enum MemberContributionKind { LifeEvent, Story, Photo }
public enum MemberContributionStatus { PendingReview, Approved, Rejected }

/// <summary>
/// Non-persisted contribution contract. Photo references are opaque identifiers;
/// upload, storage and serving remain disabled until trusted authorization exists.
/// </summary>
public sealed class MemberContribution
{
    private readonly string _privateDetails;

    public Guid Id { get; }
    public Guid FamilyId { get; }
    public Guid ContributorPrincipalId { get; }
    public Guid SubjectPersonId { get; }
    public MemberContributionKind Kind { get; }
    public Guid? MediaAssetId { get; }
    public Visibility Visibility { get; }
    public MemberContributionStatus Status { get; }

    private MemberContribution(Guid id, Guid familyId, Guid contributorPrincipalId,
        Guid subjectPersonId, MemberContributionKind kind, Guid? mediaAssetId,
        Visibility visibility, MemberContributionStatus status, string privateDetails)
    {
        Id = id;
        FamilyId = familyId;
        ContributorPrincipalId = contributorPrincipalId;
        SubjectPersonId = subjectPersonId;
        Kind = kind;
        MediaAssetId = mediaAssetId;
        Visibility = visibility;
        Status = status;
        _privateDetails = privateDetails;
    }

    public static MemberContribution Submit(Guid familyId, Guid contributorPrincipalId,
        Guid subjectPersonId, MemberContributionKind kind, string? privateDetails,
        Guid? mediaAssetId = null)
    {
        if (familyId == Guid.Empty || contributorPrincipalId == Guid.Empty ||
            subjectPersonId == Guid.Empty || !Enum.IsDefined(kind) ||
            (kind == MemberContributionKind.Photo) != (mediaAssetId is not null) ||
            mediaAssetId == Guid.Empty ||
            (kind != MemberContributionKind.Photo && string.IsNullOrWhiteSpace(privateDetails)) ||
            privateDetails?.Length > 4000)
            throw new ArgumentException("Contribution is incomplete or invalid.");

        return new(Guid.NewGuid(), familyId, contributorPrincipalId, subjectPersonId,
            kind, mediaAssetId, Visibility.PersonAndRecipient,
            MemberContributionStatus.PendingReview, privateDetails?.Trim() ?? string.Empty);
    }

    public MemberContribution Review(Guid verifiedModeratorId, Guid moderatorFamily,
        bool canModerate, bool approve)
    {
        if (verifiedModeratorId == Guid.Empty ||
            verifiedModeratorId == ContributorPrincipalId ||
            moderatorFamily != FamilyId || !canModerate ||
            Status != MemberContributionStatus.PendingReview)
            throw new UnauthorizedAccessException("Contribution review denied.");

        return new(Id, FamilyId, ContributorPrincipalId, SubjectPersonId, Kind,
            MediaAssetId, Visibility,
            approve ? MemberContributionStatus.Approved : MemberContributionStatus.Rejected,
            _privateDetails);
    }

    public bool CanPublishMedia(bool subjectConsentVerified,
        bool mediaOwnerConsentVerified, bool viewerHasFieldPermission,
        bool subjectIsMinor, bool guardianConsentVerified) =>
        Kind == MemberContributionKind.Photo &&
        Status == MemberContributionStatus.Approved &&
        GratitudePrivacyGate.CanPublishMedia(subjectConsentVerified,
            mediaOwnerConsentVerified, moderatorApproved: true, viewerHasFieldPermission,
            subjectIsMinor, guardianConsentVerified);

    public string RevealToTrustedSubmitter(Guid verifiedPrincipal) =>
        verifiedPrincipal == ContributorPrincipalId
            ? _privateDetails : throw new UnauthorizedAccessException("Contribution details denied.");
}
