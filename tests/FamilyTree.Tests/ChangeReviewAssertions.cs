using System.Runtime.CompilerServices;
using System.Reflection;
using FamilyTree.Domain;

internal static class ChangeReviewAssertions
{
    [ModuleInitializer]
    public static void Run()
    {
        var family = Guid.NewGuid();
        var author = Guid.NewGuid();
        var moderator = Guid.NewGuid();
        var request = GenealogyChangeProposal.Submit(family, author, Guid.NewGuid());
        Check(request.State == ChangeReviewState.Pending, "new edits start pending");
        Check(typeof(GenealogyChangeProposal).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0,
            "public constructors cannot mint an approved proposal");
        Check(typeof(GenealogyChangeProposal).GetProperty(nameof(GenealogyChangeProposal.State))!.SetMethod is null,
            "review state cannot be set externally");
        ExpectUnauthorized(() => request.Review(author, family, true, true),
            "self approval rejected");
        ExpectUnauthorized(() => request.Review(moderator, Guid.NewGuid(), true, true),
            "cross-family moderator rejected");
        ExpectUnauthorized(() => request.Review(moderator, family, false, true),
            "unprivileged moderator rejected");
        var accepted = request.Review(moderator, family, true, true);
        Check(accepted.State == ChangeReviewState.Approved &&
              accepted.ReviewedByPrincipalId == moderator &&
              request.State == ChangeReviewState.Pending,
              "approved transition returns immutable reviewed snapshot");
        var rejected = request.Review(moderator, family, true, false);
        Check(rejected.State == ChangeReviewState.Rejected,
            "valid moderator may reject a pending change");
        try
        {
            accepted.Review(moderator, family, true, false);
            throw new InvalidOperationException("double review accepted");
        }
        catch (InvalidOperationException e) when (e.Message == "A reviewed change cannot be reviewed again.")
        {
            // expected
        }
        Console.WriteLine("PASS genealogy change review transitions");
    }

    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private static void ExpectUnauthorized(Action action, string reason)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(reason);
    }
}
