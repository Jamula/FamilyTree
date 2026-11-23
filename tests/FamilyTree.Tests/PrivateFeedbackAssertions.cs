using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class PrivateFeedbackAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var family=Guid.NewGuid();var member=Guid.NewGuid();var moderator=Guid.NewGuid();
        const string privateText="PRIVATE NAME and synthetic email@example.invalid 555-0100";
        var feedback=PrivateFeedback.Submit(family,member,FeedbackTopic.Usability,privateText);
        Check(feedback.Disposition==FeedbackDisposition.Submitted,"submitted default");
        Check(feedback.RevealToTrustedSubmitter(member).Contains("PRIVATE",StringComparison.Ordinal),
            "submitter can see their own local feedback");
        Deny(()=>feedback.RevealToTrustedSubmitter(moderator),
            "unrelated principal cannot read private text");
        Deny(()=>feedback.Triage(member,family,true,false),"cannot self moderate");
        Deny(()=>feedback.Triage(moderator,Guid.NewGuid(),true,false),"cross family");
        Deny(()=>feedback.Triage(moderator,family,false,false),"unprivileged triage");
        var triaged=feedback.Triage(moderator,family,true,false);
        Deny(()=>feedback.PreparePublicEscalation(moderator,family,true,true),
            "untriaged feedback cannot be public");
        Deny(()=>triaged.PreparePublicEscalation(moderator,family,true,false),
            "no consent means no escalation");
        var publicDraft=triaged.PreparePublicEscalation(moderator,family,true,true);
        var serialized=System.Text.Json.JsonSerializer.Serialize(publicDraft);
        Check(!serialized.Contains("PRIVATE",StringComparison.Ordinal) &&
              !serialized.Contains("email@example",StringComparison.Ordinal) &&
              !serialized.Contains(family.ToString(),StringComparison.Ordinal) &&
              !serialized.Contains(member.ToString(),StringComparison.Ordinal),
              "only canned category content can reach public GitHub");
        Check(triaged.Triage(moderator,family,true,true).Disposition==
            FeedbackDisposition.Closed,"moderator can close");
        Reject(()=>PrivateFeedback.Submit(family,member,(FeedbackTopic)999,"test"),
            "invalid category");
        Reject(()=>PrivateFeedback.Submit(family,member,FeedbackTopic.Security,new string('x',1001)),
            "oversized private input");
        Check(typeof(PrivateFeedback).GetProperty("Details") is null &&
              typeof(PrivateFeedback).GetConstructors().Length==0,
              "private text is not exposed as a public serializable property");
        Console.WriteLine("PASS privacy-safe feedback consent, moderation and no-PII escalation");
    }
    static void Check(bool okay,string why){if(!okay)throw new InvalidOperationException(why);}
    static void Deny(Action action,string why)
    {var refused=false;try{action();}catch(UnauthorizedAccessException){refused=true;}
     Check(refused,why);}
    static void Reject(Action action,string why)
    {var refused=false;try{action();}catch(ArgumentException){refused=true;}
     Check(refused,why);}
}
