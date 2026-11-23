using FamilyTree.Domain;

namespace FamilyTree.Domain;

/// <summary>
/// No provider is configured by default. This gate never calls a model and
/// cannot write to an approved record store.
/// </summary>
public sealed record AiProposalRequest(
    Guid ViewerPrincipalId,
    Guid SubjectPersonId,
    IReadOnlyList<Guid> CitedPersonIds,
    IReadOnlyList<Relationship> CitedRelationships,
    string SuggestedText);

public sealed record PendingAiProposal(
    Guid Id, Guid SubjectPersonId, string SuggestedText,
    IReadOnlyList<Guid> CitedPersonIds, bool Approved = false);

public static class AiProposalGate
{
    /// <summary>
    /// Opt-in, permission-filtered *draft creation only*. Unauthorized and
    /// nonexistent subjects must be indistinguishable to the caller.
    /// No provider calls, persistence, logging, or automatic approval.
    /// </summary>
    public static async Task<PendingAiProposal?> TryStageAsync(
        AiProposalRequest request, IFamilyAuthorization authorization,
        bool aiEnabled = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);
        if (!aiEnabled || request.SubjectPersonId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SuggestedText) ||
            request.SuggestedText.Length > 2000 ||
            request.CitedPersonIds is null || request.CitedRelationships is null)
            return null;

        if (!await authorization.CanSeePersonAsync(request.ViewerPrincipalId,
                request.SubjectPersonId, cancellationToken))
            return null;

        var visible = request.CitedPersonIds.Append(request.SubjectPersonId).Distinct().ToArray();
        foreach (var id in visible)
            if (id == Guid.Empty || !await authorization.CanSeePersonAsync(
                    request.ViewerPrincipalId, id, cancellationToken))
                return null;

        foreach (var relation in request.CitedRelationships)
        {
            if (relation is null ||
                !visible.Contains(relation.FromPersonId) ||
                !visible.Contains(relation.ToPersonId) ||
                !await authorization.CanSeeRelationshipAsync(
                    request.ViewerPrincipalId, relation, cancellationToken))
                return null;
        }
        // A caller may display this ephemeral draft, but has no way to
        // approve or publish through this class.
        return new PendingAiProposal(Guid.NewGuid(), request.SubjectPersonId,
            request.SuggestedText.Trim(), visible);
    }
}
