using System.Diagnostics;
using FamilyTree.ServiceDefaults;

internal static class TelemetryAssertions
{
    public static void Run()
    {
        Ensure(FamilyTelemetry.ClassifyPath("/health") == FamilyOperation.Health, "health label");
        Ensure(FamilyTelemetry.ClassifyPath("/HEALTH/") == FamilyOperation.Health,
            "case and trailing slash should normalize");
        Ensure(FamilyTelemetry.ClassifyPath("/api/v1/tree/") == FamilyOperation.Genealogy,
            "tree trailing slash should normalize");
        Ensure(FamilyTelemetry.ClassifyPath("/api/v1/tree") == FamilyOperation.Genealogy,
            "genealogy label");
        Ensure(FamilyTelemetry.ClassifyPath("/api/v1/people/PRIVATE-PERSON-ID") == FamilyOperation.Other,
            "request-specific paths must never be used as operation names");

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FamilyTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = FamilyTelemetry.Start(FamilyOperation.Genealogy);
        Ensure(activity is not null, "trace should be created when a listener is active");
        var tags = activity!.TagObjects.ToArray();
        Ensure(tags.Length == 1 && tags[0].Key == "familytree.operation"
            && tags[0].Value?.ToString() == "genealogy", "trace labels must be bounded");

        // API exposes only an enum operation and a numeric status, not personal data.
        FamilyTelemetry.Record(FamilyOperation.Genealogy, 403, TimeSpan.FromMilliseconds(3));
        using (var parent = new Activity("untrusted-request"))
        {
            parent.SetBaggage("private.email", "user@example.invalid");
            parent.Start();
            Ensure(FamilyTelemetry.Start(FamilyOperation.Health) is null,
                "baggage from untrusted ambient activity must never enter our span");
        }
        Console.WriteLine("PASS telemetry privacy vocabulary and baggage assertions");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
