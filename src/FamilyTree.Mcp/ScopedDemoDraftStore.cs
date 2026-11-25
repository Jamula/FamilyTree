namespace FamilyTree.Mcp;

/// <summary>
/// Bounded, process-local partitions for fictional drafts. Scope IDs separate
/// demo buffers; they are not authenticated identities or authorization tokens.
/// </summary>
public sealed class ScopedDemoDraftStore
{
    private const int MaxScopes = 100;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DemoDraftBuffer> _scopes = new();

    public Guid CreateScope()
    {
        lock (_gate)
        {
            if (_scopes.Count >= MaxScopes)
                throw new InvalidOperationException("Draft scope limit reached.");

            var scopeId = Guid.NewGuid();
            _scopes.Add(scopeId, new DemoDraftBuffer());
            return scopeId;
        }
    }

    public DemoProposal Stage(Guid scopeId, string kind, string summary)
    {
        lock (_gate)
        {
            if (!_scopes.TryGetValue(scopeId, out var drafts))
                throw new InvalidOperationException("Draft scope is unavailable.");
            return drafts.Stage(kind, summary);
        }
    }

    public IReadOnlyList<DemoProposal> All(Guid scopeId)
    {
        lock (_gate)
            return _scopes.TryGetValue(scopeId, out var drafts)
                ? drafts.All()
                : Array.Empty<DemoProposal>();
    }

    public bool DeleteScope(Guid scopeId)
    {
        lock (_gate) return scopeId != Guid.Empty && _scopes.Remove(scopeId);
    }
}
