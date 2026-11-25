using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using FamilyTree.ServiceDefaults;
using FamilyTree.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

internal static class WebRequestTelemetryAssertions
{
    internal static async Task RunAsync()
    {
        var measurements = new List<string>();
        var spans = new List<Activity>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == FamilyTelemetry.SourceName &&
                instrument.Name == "familytree.http.requests")
                listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            measurements.Add(string.Join("|", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))));
        metrics.Start();
        using var traces = new ActivityListener
        {
            ShouldListenTo = source => source.Name == FamilyTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Add(activity)
        };
        ActivitySource.AddActivityListener(traces);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseFamilyTreeRequestTelemetry();
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/denied", () => Results.StatusCode(403));
        app.MapGet("/failure", () => Results.StatusCode(503));
        app.MapGet("/exception", (Func<IResult>)(() => throw new InvalidOperationException("synthetic-secret-message")));
        await app.StartAsync();
        using var client = app.GetTestClient();
        Check((await client.GetAsync("/health?name=synthetic-secret")).StatusCode == HttpStatusCode.OK, "success");
        Check((await client.GetAsync("/denied/synthetic-secret")).StatusCode == HttpStatusCode.NotFound, "unknown path");
        Check((await client.GetAsync("/denied")).StatusCode == HttpStatusCode.Forbidden, "denial");
        Check((await client.GetAsync("/failure")).StatusCode == HttpStatusCode.ServiceUnavailable, "failure");
        var exceptionObserved = false;
        try
        {
            await client.GetAsync("/exception");
            throw new InvalidOperationException("exception did not propagate");
        }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic-secret-message")
        {
            exceptionObserved = true;
        }
        Check(exceptionObserved, "exception propagated");

        Check(measurements.Count == 5 && measurements.Count(x => x.Contains("server_error", StringComparison.Ordinal)) == 2 &&
            measurements.Count(x => x.Contains("client_error", StringComparison.Ordinal)) == 2 &&
            measurements.Count(x => x.Contains("success", StringComparison.Ordinal)) == 1, "bounded outcomes");
        Check(spans.Count == 5 && spans.All(span => span.TagObjects.Count() == 1 &&
            span.TagObjects.Single().Key == "familytree.operation"), "bounded span tags");
        Check(measurements.All(value => !value.Contains("synthetic-secret", StringComparison.Ordinal)) &&
            spans.All(span => !span.DisplayName.Contains("synthetic-secret", StringComparison.Ordinal) &&
                span.TagObjects.All(tag => !tag.Value!.ToString()!.Contains("synthetic-secret", StringComparison.Ordinal))),
            "request and exception data excluded");
        Console.WriteLine("PASS Web request telemetry outcomes and privacy");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
