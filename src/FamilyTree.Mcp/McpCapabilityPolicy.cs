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
    PublishChanges
}

public sealed record McpCapabilityOffer(
    string AgentId,
    IReadOnlySet<McpCapability> Requested,
    IReadOnlySet<McpCapability> UserConsented);

public sealed record McpNegotiatedCapabilities(
    string AgentId,
    IReadOnlySet<McpCapability> Granted);

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
        negotiated.Granted is not null &&
        negotiated.Granted.Contains(capability);

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
