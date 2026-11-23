namespace FamilyTree.Domain;

/// <summary>
/// All boolean permissions must come from a trusted server authorization
/// service, not client-submitted flags. This is a pure policy primitive.
/// </summary>
public static class GratitudePrivacyGate
{
    public static bool CanRead(Gratitude gratitude, bool senderClaimVerified,
        bool recipientClaimVerified, bool administratorAuthorized,
        bool branchMemberAuthorized, bool senderConsent, bool recipientConsent,
        bool moderated)
    {
        ArgumentNullException.ThrowIfNull(gratitude);
        return gratitude.Visibility switch
        {
            Visibility.AdministratorsOnly => administratorAuthorized,
            Visibility.PersonAndRecipient => senderClaimVerified || recipientClaimVerified,
            Visibility.BranchMembers => (senderClaimVerified || recipientClaimVerified) ||
                (branchMemberAuthorized && senderConsent && recipientConsent && moderated),
            _ => false
        };
    }

    /// <summary>Never publish media without separate consent and moderation.</summary>
    public static bool CanPublishMedia(bool subjectConsentVerified,
        bool mediaOwnerConsentVerified, bool moderatorApproved,
        bool viewerHasFieldPermission, bool subjectIsMinor,
        bool guardianConsentVerified) =>
        subjectConsentVerified && mediaOwnerConsentVerified && moderatorApproved &&
        viewerHasFieldPermission && (!subjectIsMinor || guardianConsentVerified);
}