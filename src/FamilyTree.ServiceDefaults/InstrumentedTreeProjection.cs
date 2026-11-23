using System.Diagnostics;
using FamilyTree.Domain;

namespace FamilyTree.ServiceDefaults;

/// <summary>
/// Instrumented authorization-aware tree projection. No identifiers, text,
/// relationship labels or graph structure enter telemetry dimensions.
/// This wrapper does not grant access and does not expose API endpoints.
/// </summary>
public static class InstrumentedTreeProjection
{
    public static async Task<PersonTreeProjection?> ProjectAsync(
        Guid viewerId, Guid centerPersonId, IEnumerable<Person> people,
        IEnumerable<Relationship> relationships, TreeOptions options,
        IFamilyAuthorization authorization, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var outcome = FamilyOutcome.Failed;
        try
        {
            var result = await PersonTreeProjector.ProjectAsync(
                viewerId, centerPersonId, people, relationships, options,
                authorization, cancellationToken);
            outcome = result is null ? FamilyOutcome.Denied : FamilyOutcome.Allowed;
            return result;
        }
        finally
        {
            FamilyTelemetry.RecordWorkflow(FamilyWorkload.GraphTraversal, outcome,
                timer.Elapsed);
        }
    }
}