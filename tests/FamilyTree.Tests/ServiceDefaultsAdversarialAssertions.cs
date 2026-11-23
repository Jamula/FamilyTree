using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using FamilyTree.Domain;
using FamilyTree.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class ServiceDefaultsAdversarialAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        IConfiguration Config(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var empty = Config(new());
        Expect<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddFamilyTreeObservability(empty),
            "null telemetry services rejected");
        Expect<ArgumentNullException>(() =>
            new ServiceCollection().AddFamilyTreeObservability(null!),
            "null telemetry config rejected");

        var disabled = new ServiceCollection();
        Check(ReferenceEquals(disabled.AddFamilyTreeObservability(empty), disabled) &&
              disabled.Count == 0, "OTLP never autoenabled without explicit consent");
        var explicitOff = new ServiceCollection();
        Check(ReferenceEquals(explicitOff.AddFamilyTreeObservability(Config(new()
        {
            ["FAMILYTREE_OTLP_ENABLED"] = "false",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://private.vendor.invalid"
        })), explicitOff) && explicitOff.Count == 0,
            "explicit disable is stronger than remote endpoint hint");

        foreach (var endpoint in new string?[] { null, "", "not-an-url",
            "ftp://localhost:1234", "/relative" })
            Expect<InvalidOperationException>(() =>
                new ServiceCollection().AddFamilyTreeObservability(Config(new()
                {
                    ["FAMILYTREE_OTLP_ENABLED"] = "true",
                    ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint
                })), "bad endpoint refused without starting an exporter");

        Expect<InvalidOperationException>(() =>
            new ServiceCollection().AddFamilyTreeObservability(Config(new()
            {
                ["FAMILYTREE_OTLP_ENABLED"] = "true",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://vendor.invalid"
            })), "unapproved remote exporter denied");

        foreach (var protocol in new[] { "grpc", "http/protobuf" })
        {
            var services = new ServiceCollection();
            services.AddFamilyTreeObservability(Config(new()
            {
                ["FAMILYTREE_OTLP_ENABLED"] = "true",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol
            }));
            Check(services.Count > 0, "opt-in local SDK registrations available");
            // Do not build providers, send OTLP data or open a port in tests.
        }
        // Explicit opt-in remote path is a configuration branch test only.
        // No ServiceProvider/exporter is constructed; no network traffic occurs.
        var approvedRemote = new ServiceCollection();
        approvedRemote.AddFamilyTreeObservability(Config(new()
        {
            ["FAMILYTREE_OTLP_ENABLED"] = "true",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://vendor.invalid",
            ["FAMILYTREE_REMOTE_OTLP_APPROVED"] = "true"
        }));
        Check(approvedRemote.Count > 0, "explicit approval allows configuration only");

        Check(FamilyTelemetry.ClassifyPath(null) == FamilyOperation.Other &&
              FamilyTelemetry.ClassifyPath("/HEALTH/") == FamilyOperation.Health &&
              FamilyTelemetry.ClassifyPath("/api/v1/capabilities") == FamilyOperation.Capabilities &&
              FamilyTelemetry.ClassifyPath("/api/v1/people") == FamilyOperation.Genealogy &&
              FamilyTelemetry.ClassifyPath("/api/v1/people/secret123") == FamilyOperation.Other,
              "classification discards private path details");

        var requestLabels = new List<string>();
        var workflowLabels = new List<string>();
        var durations = new List<double>();
        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, metricListener) =>
            {
                if (instrument.Meter.Name == FamilyTelemetry.SourceName)
                    metricListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                var value = string.Join("|", tags.ToArray().Select(t => t.Key + ":" + t.Value));
                if (instrument.Name == "familytree.http.requests") requestLabels.Add(value);
                if (instrument.Name == "familytree.workflow.operations") workflowLabels.Add(value);
            });
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            {
                if (instrument.Name is "familytree.http.duration" or "familytree.workflow.duration")
                    durations.Add(value);
            });
            listener.Start();

            FamilyTelemetry.Record(FamilyOperation.Health, 200, TimeSpan.Zero);
            FamilyTelemetry.Record(FamilyOperation.Capabilities, 401, TimeSpan.FromMilliseconds(2));
            FamilyTelemetry.Record((FamilyOperation)999, 503, TimeSpan.FromMilliseconds(1));
            foreach (var work in Enum.GetValues<FamilyWorkload>())
                foreach (var outcome in Enum.GetValues<FamilyOutcome>())
                    FamilyTelemetry.RecordWorkflow(work, outcome, TimeSpan.FromMilliseconds(1));
            FamilyTelemetry.RecordAiTokens(0);
        }
        Check(requestLabels.Count == 3 && requestLabels.Any(x => x.Contains("success", StringComparison.Ordinal)) &&
              requestLabels.Any(x => x.Contains("client_error", StringComparison.Ordinal)) &&
              requestLabels.Any(x => x.Contains("server_error", StringComparison.Ordinal)) &&
              requestLabels.All(x => !x.Contains("secret123", StringComparison.Ordinal)),
            "request metrics use bounded, non-identifying results");
        Check(workflowLabels.Count == Enum.GetValues<FamilyWorkload>().Length *
              Enum.GetValues<FamilyOutcome>().Length &&
              workflowLabels.All(x => !x.Contains("secret", StringComparison.OrdinalIgnoreCase)),
            "all workflow labels bounded and safe");
        Check(durations.Count >= 3 && durations.All(x => x >= 0),
            "duration metrics never negative");

        Expect<ArgumentOutOfRangeException>(() =>
            FamilyTelemetry.Record(FamilyOperation.Health, 0, TimeSpan.Zero),
            "invalid HTTP status rejected");
        Expect<ArgumentOutOfRangeException>(() =>
            FamilyTelemetry.Record(FamilyOperation.Health, 600, TimeSpan.Zero),
            "out-of-range HTTP status rejected");
        Expect<ArgumentOutOfRangeException>(() =>
            FamilyTelemetry.Record(FamilyOperation.Health, 200, TimeSpan.FromSeconds(-1)),
            "negative request duration rejected");
        Expect<ArgumentOutOfRangeException>(() =>
            FamilyTelemetry.RecordWorkflow(FamilyWorkload.AiRequest, FamilyOutcome.Allowed,
                TimeSpan.FromTicks(-1)), "negative workflow duration rejected");
        Expect<ArgumentOutOfRangeException>(() =>
            FamilyTelemetry.RecordWorkflow((FamilyWorkload)999, FamilyOutcome.Allowed,
                TimeSpan.Zero), "undefined workload refused");
        Expect<ArgumentOutOfRangeException>(() =>
            FamilyTelemetry.RecordWorkflow(FamilyWorkload.AiRequest, (FamilyOutcome)999,
                TimeSpan.Zero), "undefined outcome refused");
        Expect<ArgumentOutOfRangeException>(() => FamilyTelemetry.RecordAiTokens(-5),
            "negative token count rejected");

        var principal = Guid.NewGuid();
        var person = new Person(Guid.NewGuid(), "Private Synthetic Identity");
        var faults = new FaultingAuthorization();
        Expect<InvalidOperationException>(() => InstrumentedTreeProjection.ProjectAsync(
                principal, person.Id, [person], [], new TreeOptions(), faults)
                .GetAwaiter().GetResult(),
            "authorization exception propagates without data leakage");
        Expect<OperationCanceledException>(() => InstrumentedTreeProjection.ProjectAsync(
                principal, person.Id, [person], [], new TreeOptions(), faults,
                new CancellationToken(true)).GetAwaiter().GetResult(),
            "cancelled authorized projection propagates cancellation");

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FamilyTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(activityListener);
        using (var cleanParent = new Activity("clean-parent"))
        {
            cleanParent.SetIdFormat(ActivityIdFormat.W3C);
            cleanParent.Start();
            using var cleanChild = FamilyTelemetry.Start(FamilyOperation.Health);
            Check(cleanChild is not null,
                "clean parent must produce a sampled span or privacy test is meaningless");
        }
        using (var parent = new Activity("untrusted-request"))
        {
            parent.SetIdFormat(ActivityIdFormat.W3C);
            parent.Start();
            parent.TraceStateString = "private=synthetic";
            Check(FamilyTelemetry.Start(FamilyOperation.Other) is null,
                "tracestate from inbound requests never enters a new span");
        }
        Console.WriteLine("PASS service defaults privacy metrics, opt-in endpoints, failures and cancellations");
    }

    private sealed class FaultingAuthorization : IFamilyAuthorization
    {
        public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new InvalidOperationException("Synthetic denial failure"));

        public Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relationship,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
    private static void Check(bool result, string message)
    { if(!result) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch(T) { return; }
        throw new InvalidOperationException(message);
    }
}
