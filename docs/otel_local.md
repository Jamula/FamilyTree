# OpenTelemetry setup with the $0 privacy guard

ServiceDefaults uses stable OpenTelemetry 1.21.0. Native `FamilyTelemetry` instruments use only allowlisted, bounded labels. The OpenTelemetry SDK/exporter is **off by default**. No remote logs are exported.

## Opt-in local Aspire Dashboard
When the Aspire AppHost is available (#23), it orchestrates both applications and sets `FAMILYTREE_OTLP_ENABLED=true` and its **localhost** OTLP endpoint for local development. The OTel exporter supports both gRPC and HTTP/protobuf, chosen by `OTEL_EXPORTER_OTLP_PROTOCOL`. Dashboard uses synthetic requests only.

## Without Aspire
Run the API or Web without any OTLP configuration and no exporter is created. To opt in, configure a collector listening on the local machine:
```bash
FAMILYTREE_OTLP_ENABLED=true \
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
dotnet run --project src/FamilyTree.Api/FamilyTree.Api.csproj
```
For PowerShell, set corresponding process environment variables with `$env:VARIABLE = "value"` and run `dotnet run`.

## Privacy and costs
- No default telemetry external connections; local enablement requires both a flag and a valid endpoint.
- Non-loopback exporters are rejected unless `FAMILYTREE_REMOTE_OTLP_APPROVED=true`. This switch is **not** itself approval to incur charges; it can be set only after explicit owner permission and a privacy review.
- No HTTP URL, query, token, person ID, family relationship, GEDCOM payload, AI prompt or email is included in FamilyTree-specific span or metric tags.
- External OpenTelemetry auto-instrumentation or SDK logging is **not** enabled; it may emit identifiers and must undergo a privacy test before use.
- Tracing and metrics use W3C Activity correlation. Untrusted inherited baggage/tracestate prevents an app-specific span from being created.
- The API routes exposing genealogy remain fail-closed until real authorization is implemented.

## Operational follow-ups
Before hosted operation define trace/metric retention, sampling, redaction, monitoring cost caps, exporter TLS, backend availability and workload-specific instrumentation. Never create a managed monitoring resource without approval.
