using System.Runtime.CompilerServices;
using System.Diagnostics.Metrics;
using FamilyTree.Domain;
using FamilyTree.ServiceDefaults;

internal static class InstrumentedTreeAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var captured = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == FamilyTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (instrument.Name == "familytree.workflow.operations")
                captured.Add(string.Join("|",tags.ToArray().Select(t => t.Key + ":" + t.Value)));
        });
        listener.Start();

        var center = Guid.NewGuid();
        var privateName = "A private fictional identity";
        var person = new Person(center, privateName);
        var viewer = Guid.NewGuid();
        var auth = new StubAuth(center);
        var allowed = InstrumentedTreeProjection.ProjectAsync(
            viewer, center, new[] { person }, Array.Empty<Relationship>(),
            new TreeOptions(), auth).GetAwaiter().GetResult();
        var denied = InstrumentedTreeProjection.ProjectAsync(
            viewer, Guid.NewGuid(), new[] { person }, Array.Empty<Relationship>(),
            new TreeOptions(), auth).GetAwaiter().GetResult();
        if (allowed is null || denied is not null || captured.Count != 2 ||
            !captured.Any(x => x.Contains("familytree.outcome:allowed", StringComparison.Ordinal)) ||
            !captured.Any(x => x.Contains("familytree.outcome:denied", StringComparison.Ordinal)) ||
            captured.Any(x => x.Contains(privateName, StringComparison.Ordinal) ||
                              x.Contains(center.ToString(), StringComparison.Ordinal)))
            throw new InvalidOperationException("Graph workflow telemetry must not leak identities.");
        Console.WriteLine("PASS instrumented graph workflow privacy assertions");
    }

    private sealed class StubAuth(Guid visible) : IFamilyAuthorization
    {
        public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(personId == visible);
        public Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relationship,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}