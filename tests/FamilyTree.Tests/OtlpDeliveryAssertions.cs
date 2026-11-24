using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using FamilyTree.ServiceDefaults;
using FamilyTree.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

internal static class OtlpDeliveryAssertions
{
    internal static async Task RunAsync()
    {
        var payloads = new ConcurrentQueue<byte[]>();
        var collectorBuilder = WebApplication.CreateBuilder();
        collectorBuilder.Logging.ClearProviders();
        collectorBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var collector = collectorBuilder.Build();
        collector.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            payloads.Enqueue(body.ToArray());
            context.Response.StatusCode = 200;
        });
        await collector.StartAsync();
        var endpoint = collector.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FAMILYTREE_OTLP_ENABLED"] = "true",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf"
        });
        builder.Services.AddFamilyTreeObservability(builder.Configuration);
        await using var app = builder.Build();
        app.UseFamilyTreeRequestTelemetry();
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/denied", () => Results.StatusCode(403));
        app.MapGet("/failure", () => Results.StatusCode(503));
        app.MapGet("/exception", (Func<IResult>)(() => throw new InvalidOperationException("synthetic-exception-marker")));
        app.MapPost("/submit", () => Results.Ok());
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var parent = new Activity("synthetic-server-parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        Check(!parent.Recorded, "synthetic parent is not recorded");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health?value=synthetic-query-marker");
        request.Headers.Add("X-Synthetic", "synthetic-header-marker");
        Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.OK, "success request");
        Check(app.Services.GetRequiredService<TracerProvider>().ForceFlush(10000), "initial trace export");
        Check(payloads.Any(bytes => Encoding.UTF8.GetString(bytes).Contains("familytree.http", StringComparison.Ordinal)),
            "trace with unsampled server parent");
        using var sensitive = new HttpRequestMessage(HttpMethod.Post, "/submit?name=synthetic-name-marker")
        {
            Content = new StringContent("synthetic-payload-marker")
        };
        sensitive.Headers.Add("X-Synthetic-Token", "synthetic-token-marker");
        sensitive.Headers.Add("baggage", "private=synthetic-baggage-marker");
        sensitive.Headers.Add("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        sensitive.Headers.Add("tracestate", "private=synthetic-tracestate-marker");
        Check((await client.SendAsync(sensitive)).StatusCode == HttpStatusCode.OK, "sensitive synthetic request");
        Check((await client.GetAsync("/denied")).StatusCode == HttpStatusCode.Forbidden, "denied request");
        Check((await client.GetAsync("/failure")).StatusCode == HttpStatusCode.ServiceUnavailable, "failed request");
        var exceptionObserved = false;
        try { await client.GetAsync("/exception"); }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic-exception-marker")
        {
            exceptionObserved = true;
        }
        Check(exceptionObserved, "exception request");
        Check((await client.GetAsync("/synthetic-path-marker")).StatusCode == HttpStatusCode.NotFound, "unknown request");

        Check(app.Services.GetRequiredService<TracerProvider>().ForceFlush(10000), "trace export");
        Check(app.Services.GetRequiredService<MeterProvider>().ForceFlush(10000), "metric export");
        var batches = payloads.Select(Encoding.UTF8.GetString).ToArray();
        var exported = string.Concat(batches);
        Check(batches.Any(batch => batch.Contains("familytree.http", StringComparison.Ordinal) &&
            !batch.Contains("familytree.http.requests", StringComparison.Ordinal)) &&
            batches.Any(batch => batch.Contains("familytree.http.requests", StringComparison.Ordinal)) &&
            exported.Contains("familytree.http.requests", StringComparison.Ordinal) &&
            exported.Contains("familytree.http.duration", StringComparison.Ordinal) &&
            exported.Contains("success", StringComparison.Ordinal) &&
            exported.Contains("client_error", StringComparison.Ordinal) &&
            exported.Contains("server_error", StringComparison.Ordinal), "trace and metric delivery");
        foreach (var marker in new[] { "synthetic-query-marker", "synthetic-header-marker",
            "synthetic-exception-marker", "synthetic-path-marker", "synthetic-name-marker",
            "synthetic-payload-marker", "synthetic-token-marker", "synthetic-baggage-marker",
            "synthetic-tracestate-marker" })
            Check(!exported.Contains(marker, StringComparison.Ordinal), "sensitive input excluded from OTLP");

        await collector.StopAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Check((await client.GetAsync("/health", timeout.Token)).StatusCode == HttpStatusCode.OK,
            "application remains available with unreachable collector");
        _ = app.Services.GetRequiredService<TracerProvider>().ForceFlush(3000);
        _ = app.Services.GetRequiredService<MeterProvider>().ForceFlush(3000);
        Check(!timeout.IsCancellationRequested, "unreachable collector bounded");
        Console.WriteLine("PASS loopback OTLP delivery, redaction and unreachable collector");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
