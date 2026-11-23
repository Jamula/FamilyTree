using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FamilyTree.ServiceDefaults;

/// <summary>
/// Privacy-first opt-in OpenTelemetry. Default = no SDK exporter, no network output.
/// Local Aspire can opt in using OTLP over localhost. Remote OTLP requires
/// separate explicit, reviewed enablement in addition to endpoint configuration.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddFamilyTreeObservability(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!bool.TryParse(configuration["FAMILYTREE_OTLP_ENABLED"], out var enabled) || !enabled)
            return services;

        var endpointText = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Opt-in OTLP requires an explicit HTTP(S) OTEL_EXPORTER_OTLP_ENDPOINT.");
        }

        // No silent remote exporting. The remote-approval switch must only be
        // set following an explicit owner-approved price and privacy review.
        if (!endpoint.IsLoopback &&
            !string.Equals(configuration["FAMILYTREE_REMOTE_OTLP_APPROVED"],
                "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Remote OTLP is disabled until a specific privacy and cost approval is given.");
        }

        var protocol = string.Equals(configuration["OTEL_EXPORTER_OTLP_PROTOCOL"],
            "http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource(FamilyTelemetry.SourceName)
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = endpoint;
                    options.Protocol = protocol;
                }))
            .WithMetrics(metrics => metrics
                .AddMeter(FamilyTelemetry.SourceName)
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = endpoint;
                    options.Protocol = protocol;
                }));

        // Do not export framework/application logs. Their arbitrary text could
        // include names, user-supplied values or path/query contents.
        return services;
    }
}
