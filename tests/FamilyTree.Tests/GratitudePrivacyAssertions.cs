using System.Runtime.CompilerServices;
using FamilyTree.Domain;
internal static class GratitudePrivacyAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var message = new Gratitude(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fictional");
        Check(message.Visibility == Visibility.PersonAndRecipient, "private default");
        Check(!GratitudePrivacyGate.CanRead(message, false,false,false,true,true,true,true),
              "branch member cannot see recipient-private text");
        Check(GratitudePrivacyGate.CanRead(message, false,true,false,false,false,false,false),
              "recipient can read");
        var shared = message with { Visibility = Visibility.BranchMembers };
        Check(!GratitudePrivacyGate.CanRead(shared,false,false,false,true,true,false,true),
              "both sides must consent");
        Check(!GratitudePrivacyGate.CanRead(shared,false,false,false,true,true,true,false),
              "moderation required");
        Check(GratitudePrivacyGate.CanRead(shared,false,false,false,true,true,true,true),
              "consent and moderation permit branch view");
        Check(!GratitudePrivacyGate.CanPublishMedia(true,true,true,true,true,false),
              "minor media needs guardian consent");
        Check(!GratitudePrivacyGate.CanPublishMedia(true,true,false,true,false,false),
              "no media without moderation");
        Console.WriteLine("PASS gratitude and media privacy consent checks");
    }
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}