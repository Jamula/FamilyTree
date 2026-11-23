using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FamilyTree.ServiceDefaults;

/// <summary>
/// A bounded telemetry vocabulary. Do not accept caller-supplied labels, person
/// identifiers, full routes, prompts, names, dates, headers or record content.
/// </summary>
public enum FamilyOperation
{
    Health,
    Capabilities,
    Genealogy,
    Other
}

public static class FamilyTelemetry
{
    public const string SourceName = "FamilyTree.Api";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> RequestCounter =
        Meter.CreateCounter<long>("familytree.http.requests");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("familytree.http.duration", unit: "ms");

    public static FamilyOperation ClassifyPath(string? path)
    {
        var route = (path ?? string.Empty).TrimEnd('/').ToLowerInvariant();
        return route switch
        {
            "/health" => FamilyOperation.Health,
            "/api/v1/capabilities" => FamilyOperation.Capabilities,
            "/api/v1/people" or "/api/v1/tree" => FamilyOperation.Genealogy,
            _ => FamilyOperation.Other
        };
    }

    public static Activity? Start(FamilyOperation operation)
    {
        // Normalized operation labels only; path and user input are never attached.
        // Fail closed for untrusted inbound baggage/tracestate: ActivitySource would
        // inherit it and expose sensitive values before tags could be scrubbed.
        var parent = Activity.Current;
        if (parent is not null && (parent.Baggage.Any() || !string.IsNullOrEmpty(parent.TraceStateString)))
            return null;
        var activity = ActivitySource.StartActivity("familytree.http", ActivityKind.Internal);
        activity?.SetTag("familytree.operation", ToLabel(operation));
        return activity;
    }

    public static void Record(FamilyOperation operation, int statusCode, TimeSpan elapsed)
    {
        var operationLabel = ToLabel(operation);
        var result = statusCode >= 500 ? "server_error" :
            statusCode >= 400 ? "client_error" : "success";
        var tags = new TagList
        {
            { "familytree.operation", operationLabel },
            { "familytree.result", result }
        };
        RequestCounter.Add(1, tags);
        Duration.Record(elapsed.TotalMilliseconds, tags);
    }

    private static string ToLabel(FamilyOperation operation) => operation switch
    {
        FamilyOperation.Health => "health",
        FamilyOperation.Capabilities => "capabilities",
        FamilyOperation.Genealogy => "genealogy",
        _ => "other"
    };
}
