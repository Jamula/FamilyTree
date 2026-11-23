using FamilyTree.Domain;

internal static class AiProposalAssertions
{
    public static async Task RunAsync()
    {
        var viewer = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var secret = Guid.NewGuid();
        var friend = Relationship.Create(a, b, RelationKind.Friend);
        var leak = Relationship.Create(a, secret, RelationKind.Friend);
        var policy = new FakeAuth(new HashSet<Guid> { a, b },
            new HashSet<Guid> { friend.Id });
        var safe = new AiProposalRequest(viewer, a, new[] { b },
            new[] { friend }, "  Fictional suggestion  ");
        Check(await AiProposalGate.TryStageAsync(safe, policy) is null,
            "AI off by default");
        var proposed = await AiProposalGate.TryStageAsync(safe, policy, aiEnabled: true);
        Check(proposed is not null && !proposed.Approved &&
              proposed.SuggestedText == "Fictional suggestion" &&
              proposed.CitedPersonIds.Contains(a),
              "allowed proposal is ephemeral and never approved");
        Check(await AiProposalGate.TryStageAsync(
            safe with { SubjectPersonId = secret }, policy, aiEnabled: true) is null,
            "hidden subject denied");
        Check(await AiProposalGate.TryStageAsync(
            safe with { CitedPersonIds = new[] { secret } }, policy, aiEnabled: true) is null,
            "hidden cited person denied");
        Check(await AiProposalGate.TryStageAsync(
            safe with { CitedRelationships = new[] { leak } }, policy, aiEnabled: true) is null,
            "hidden cited relationship denied");
        var deniedVisibleLink = Relationship.Create(a, b, RelationKind.Neighbor);
        Check(await AiProposalGate.TryStageAsync(
            safe with { CitedRelationships = new[] { deniedVisibleLink } }, policy,
            aiEnabled: true) is null, "relationship denied despite visible endpoints");
        Check(await AiProposalGate.TryStageAsync(
            safe with { SuggestedText = new string('x', 2001) }, policy,
            aiEnabled: true) is null, "oversize suggestion denied");
        Console.WriteLine("PASS opt-in AI proposal authorization negative tests");
    }

    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private sealed class FakeAuth(HashSet<Guid> people, HashSet<Guid> edges) : IFamilyAuthorization
    {
        public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(people.Contains(personId));
        public Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(edges.Contains(relation.Id));
    }
}
