using System.Collections.Frozen;
using System.Text.Json;

namespace FamilyTree.Mcp;

public enum McpCapability
{
    ReadSyntheticDemo,
    CreateLocalDraft,
    ReadLocalDrafts,
    ReadPrivateFamily,
    ProposePrivateChange,
    ReviewLocalDraft,
    ApproveLocalDraft,
    PublishChanges
}

public sealed record McpCapabilityOffer(
    string AgentId,
    IReadOnlySet<McpCapability> Requested,
    IReadOnlySet<McpCapability> UserConsented);

public sealed class McpNegotiatedCapabilities
{
    private readonly object _gate = new();
    private readonly FrozenSet<McpCapability> _initialGrants;
    private readonly HashSet<McpCapability> _revoked = new();

    public McpNegotiatedCapabilities(string agentId,
        IReadOnlySet<McpCapability> granted)
    {
        AgentId = agentId;
        _initialGrants = (granted ?? Array.Empty<McpCapability>().ToFrozenSet())
            .ToFrozenSet();
    }

    public string AgentId { get; }

    public IReadOnlySet<McpCapability> Granted
    {
        get
        {
            lock (_gate)
                return _initialGrants.Where(capability => !_revoked.Contains(capability))
                    .ToFrozenSet();
        }
    }

    public bool Revoke(McpCapability capability)
    {
        lock (_gate)
            return _initialGrants.Contains(capability) && _revoked.Add(capability);
    }

    public void RevokeAll()
    {
        lock (_gate) _revoked.UnionWith(_initialGrants);
    }

    internal bool Allows(McpCapability capability)
    {
        lock (_gate)
            return _initialGrants.Contains(capability) && !_revoked.Contains(capability);
    }
}

/// <summary>
/// Negotiates only local synthetic capabilities. Private access and publication
/// remain disabled until integrated server-side authorization is reviewed.
/// </summary>
public static class McpCapabilityPolicy
{
    private static readonly FrozenSet<McpCapability> LocalCapabilities =
        new[]
        {
            McpCapability.ReadSyntheticDemo,
            McpCapability.CreateLocalDraft,
            McpCapability.ReadLocalDrafts
        }.ToFrozenSet();

    private static readonly FrozenSet<McpCapability> NoCapabilities =
        Array.Empty<McpCapability>().ToFrozenSet();

    public static McpNegotiatedCapabilities Negotiate(McpCapabilityOffer? offer)
    {
        if (offer is null || string.IsNullOrWhiteSpace(offer.AgentId) ||
            offer.AgentId.Length > 80 || offer.AgentId.Any(char.IsControl) ||
            offer.Requested is null || offer.UserConsented is null)
            return new(string.Empty, NoCapabilities);

        var granted = offer.Requested
            .Where(LocalCapabilities.Contains)
            .Where(offer.UserConsented.Contains)
            .ToFrozenSet();
        return new(offer.AgentId, granted);
    }

    public static bool Allows(McpNegotiatedCapabilities? negotiated,
        McpCapability capability) =>
        negotiated is not null &&
        LocalCapabilities.Contains(capability) &&
        negotiated.Allows(capability);

    public static bool AllowsPrivateAccess(McpNegotiatedCapabilities? negotiated) => false;
}

public static class McpToolErrors
{
    public static string Redact() => "The request could not be completed.";

    public static string Invoke(Func<string> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try { return operation(); }
        catch (ArgumentException) { return ErrorResponse(); }
        catch (InvalidOperationException) { return ErrorResponse(); }
    }

    private static string ErrorResponse() =>
        JsonSerializer.Serialize(new { error = Redact() });
}
