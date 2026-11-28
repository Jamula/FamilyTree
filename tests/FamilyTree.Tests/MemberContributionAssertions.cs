using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class MemberContributionAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var family = Guid.NewGuid();
        var contributor = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var moderator = Guid.NewGuid();
        const string privateText = "Synthetic story text only.";

        var story = MemberContribution.Submit(family, contributor, subject,
            MemberContributionKind.Story, privateText);
        Check(story.Visibility == Visibility.PersonAndRecipient &&
            story.Status == MemberContributionStatus.PendingReview,
            "contributions begin private and pending review");
        Check(story.RevealToTrustedSubmitter(contributor) == privateText,
            "contributor can read private submission text");
        Deny(() => story.RevealToTrustedSubmitter(moderator),
            "unrelated principal cannot read private submission text");
        Deny(() => story.Review(contributor, family, true, true),
            "contributor cannot self-review");
        Deny(() => story.Review(moderator, Guid.NewGuid(), true, true),
            "cross-family review is denied");
        Deny(() => story.Review(moderator, family, false, true),
            "unprivileged review is denied");

        var approvedStory = story.Review(moderator, family, true, true);
        Check(approvedStory.Status == MemberContributionStatus.Approved,
            "verified moderator can approve a contribution");
        Deny(() => approvedStory.Review(moderator, family, true, false),
            "reviewed contribution cannot be reviewed again");

        var photo = MemberContribution.Submit(family, contributor, subject,
            MemberContributionKind.Photo, null, Guid.NewGuid());
        var approvedPhoto = photo.Review(moderator, family, true, true);
        Check(approvedPhoto.CanPublishMedia(true, true, true, false, false),
            "approved photo requires valid consent and viewer permission");
        Check(!approvedPhoto.CanPublishMedia(subjectConsentVerified: false,
                mediaOwnerConsentVerified: true, viewerHasFieldPermission: true,
                subjectIsMinor: true, guardianConsentVerified: true),
            "photo requires subject consent");
        Check(!approvedPhoto.CanPublishMedia(subjectConsentVerified: true,
                mediaOwnerConsentVerified: false, viewerHasFieldPermission: true,
                subjectIsMinor: true, guardianConsentVerified: true),
            "photo requires media owner consent");
        Check(!approvedPhoto.CanPublishMedia(true, true, false, false, false),
            "photo requires viewer field permission");
        Check(!approvedPhoto.CanPublishMedia(true, true, true, true, false),
            "minor photo requires guardian consent");
        Check(!photo.CanPublishMedia(true, true, true, false, false),
            "pending photo cannot be published");
        Check(!approvedStory.CanPublishMedia(true, true, true, false, false),
            "stories cannot be treated as media assets");

        var rejectedPhoto = photo.Review(moderator, family, true, false);
        Check(!rejectedPhoto.CanPublishMedia(true, true, true, false, false),
            "rejected photo cannot be published");

        var serialized = System.Text.Json.JsonSerializer.Serialize(story);
        Check(!serialized.Contains(privateText, StringComparison.Ordinal) &&
            typeof(MemberContribution).GetProperty("PrivateDetails") is null &&
            typeof(MemberContribution).GetConstructors().Length == 0,
            "private contribution text is not publicly serialized");

        Reject(() => MemberContribution.Submit(Guid.Empty, contributor, subject,
            MemberContributionKind.Story, privateText), "family is required");
        Reject(() => MemberContribution.Submit(family, contributor, subject,
            MemberContributionKind.Photo, null), "photo requires an opaque asset reference");
        Reject(() => MemberContribution.Submit(family, contributor, subject,
            MemberContributionKind.Photo, null, Guid.Empty),
            "photo requires a non-empty asset reference");
        Reject(() => MemberContribution.Submit(family, contributor, subject,
            MemberContributionKind.Story, privateText, Guid.NewGuid()),
            "non-photo cannot carry a media reference");
        Reject(() => MemberContribution.Submit(family, contributor, subject,
            (MemberContributionKind)999, privateText), "invalid contribution kind");
        Reject(() => MemberContribution.Submit(family, contributor, subject,
            MemberContributionKind.LifeEvent, new string('x', 4001)),
            "oversized contribution is rejected");

        Console.WriteLine("PASS private contribution review and media consent contracts");
    }

    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private static void Deny(Action action, string reason)
    {
        var refused = false;
        try { action(); }
        catch (UnauthorizedAccessException) { refused = true; }
        Check(refused, reason);
    }

    private static void Reject(Action action, string reason)
    {
        var refused = false;
        try { action(); }
        catch (ArgumentException) { refused = true; }
        Check(refused, reason);
    }
}
