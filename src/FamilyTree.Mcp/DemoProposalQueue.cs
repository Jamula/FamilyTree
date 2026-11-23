namespace FamilyTree.Mcp;

/// <summary>
/// Ephemeral in-memory drafts for a fictional demo only; restart clears all.
/// Never treat proposed records as authorized, approved, or persisted family data.
/// </summary>
public static class DemoProposalQueue
{
    private static readonly object Gate = new();
    private static readonly List<DemoProposal> Drafts = new();
    private const int MaxDrafts = 100;

    public static DemoProposal Stage(string kind, string summary)
    {
        // Only the known synthetic proposal types and bounded, printable
        // summaries are accepted. No arbitrary payloads, private data or logs.
        if ((kind != "person" && kind != "relationship") ||
            string.IsNullOrWhiteSpace(summary) || summary.Length > 256 ||
            summary.Any(char.IsControl))
            throw new ArgumentException("Invalid fictional proposal.");

        lock (Gate)
        {
            if (Drafts.Count >= MaxDrafts)
                throw new InvalidOperationException("Draft queue is full; restart to clear the demo.");
            var draft = new DemoProposal(Guid.NewGuid(), kind, summary, "pending",
                Persisted: false, Approved: false);
            Drafts.Add(draft);
            return draft;
        }
    }

    public static IReadOnlyList<DemoProposal> All()
    {
        lock (Gate)
            return Drafts.ToArray();
    }
}

public sealed record DemoProposal(Guid Id, string Kind, string Summary,
    string Status, bool Persisted, bool Approved);
