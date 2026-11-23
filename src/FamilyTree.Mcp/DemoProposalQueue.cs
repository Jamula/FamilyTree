namespace FamilyTree.Mcp;

/// <summary>
/// Ephemeral fictional-only draft queue. An instance supports isolated
/// concurrency/overflow testing without changing global tool state.
/// It cannot approve or persist a genealogy record.
/// </summary>
public sealed class DemoDraftBuffer
{
    private readonly object _gate = new();
    private readonly List<DemoProposal> _drafts = new();
    private const int MaxDrafts = 100;

    public DemoProposal Stage(string kind, string summary)
    {
        if ((kind != "person" && kind != "relationship") ||
            string.IsNullOrWhiteSpace(summary) || summary.Length > 256 ||
            summary.Any(char.IsControl))
            throw new ArgumentException("Invalid fictional proposal.");

        lock (_gate)
        {
            if (_drafts.Count >= MaxDrafts)
                throw new InvalidOperationException("Draft queue is full; restart to clear the demo.");
            var draft = new DemoProposal(Guid.NewGuid(), kind, summary, "pending",
                Persisted: false, Approved: false);
            _drafts.Add(draft);
            return draft;
        }
    }

    public IReadOnlyList<DemoProposal> All()
    {
        lock (_gate) return _drafts.ToArray();
    }
}

/// <summary>
/// Legacy synthetic MCP process singleton. No real family store or approval
/// endpoint may ever be attached to this public tool facade.
/// </summary>
public static class DemoProposalQueue
{
    private static readonly DemoDraftBuffer Queue = new();

    public static DemoProposal Stage(string kind, string summary) =>
        Queue.Stage(kind, summary);

    public static IReadOnlyList<DemoProposal> All() => Queue.All();
}

public sealed record DemoProposal(Guid Id, string Kind, string Summary,
    string Status, bool Persisted, bool Approved);
