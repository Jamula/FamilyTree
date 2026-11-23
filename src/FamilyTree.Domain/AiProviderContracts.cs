namespace FamilyTree.Domain;

/// <summary>
/// Provider-neutral interface for opt-in draft suggestions. Production
/// callers must run the authorized retrieval/proposal gate before invoking
/// any provider. This contract does not connect to a model.
/// </summary>
public sealed record SyntheticSuggestionInput(string FictionalContext, string Prompt);
public sealed record GeneratedSuggestion(string DraftText, string ProviderName);

public interface IGenealogySuggestionProvider
{
    Task<GeneratedSuggestion> SuggestSyntheticAsync(SyntheticSuggestionInput input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Safe default: no network calls, no model keys, and no inference.
/// </summary>
public sealed class DisabledGenealogySuggestionProvider : IGenealogySuggestionProvider
{
    public Task<GeneratedSuggestion> SuggestSyntheticAsync(SyntheticSuggestionInput input,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GeneratedSuggestion>(new InvalidOperationException(
            "AI is disabled until an authorized provider is explicitly configured."));
}

public static class SyntheticProviderGate
{
    public static async Task<GeneratedSuggestion?> TrySuggestAsync(
        IGenealogySuggestionProvider provider, SyntheticSuggestionInput input,
        bool explicitOptIn, bool permissionChecked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(input);
        if (!explicitOptIn || !permissionChecked ||
            input.FictionalContext is null ||
            string.IsNullOrWhiteSpace(input.Prompt) ||
            input.FictionalContext.Length > 8000 || input.Prompt.Length > 1000)
            return null;
        // Only synthetic input may enter this pre-MVP interface.
        // Real-family provider contracts must be separately designed and reviewed.
        return await provider.SuggestSyntheticAsync(input, ct);
    }
}