# Aspire 17 local development, no cloud charges (#23)

Aspire 17 provides a local developer dashboard and project orchestration. This project uses **free .NET 11**, **Aspire.Hosting.AppHost 17.0.0** and **Aspire.Hosting.Dotnet 17.0.0**. In 17.0 the modern supported API is `AddDotnetProject`; legacy `AddProject` is obsolete.

## Single command (Windows, macOS or Linux)
```sh
dotnet run --project src/FamilyTree.AppHost/FamilyTree.AppHost.csproj
```
The AppHost resolves the matching free Aspire CLI with DNX. If the CLI is already installed, `aspire run` is also supported; see [Aspire setup](https://aspire.dev/get-started/aspire-sdk/).

AppHost coordinates:
- API on localhost port 5181, `/health` and `/api/v1/capabilities`.
- Web on localhost port 5182, public landing page and `/health`.
- Aspire local Dashboard for resource status, traces and metrics.

Only **synthetic** requests are permitted; no production family data or secrets. Private genealogy API endpoints deliberately respond HTTP 501 until membership and branch privacy are fully implemented.

## Observability
API and Web use native `FamilyTelemetry` tracing and metrics plus the opt-in OTLP exporter (tracked in #22). This AppHost opts both in via `FAMILYTREE_OTLP_ENABLED=true` and uses Aspire's loopback OTLP service configuration. If the dashboard does not show telemetry, verify its localhost OTLP endpoint is present; never substitute a remote vendor endpoint without explicit approval. Only bounded custom instruments are exported through this SDK configuration; framework request traces and application logs are not configured for OTLP export. Aspire can separately display process output, which is not guaranteed to be scrubbed; use synthetic data only.

## Containerless mode
No Docker, Postgres, Redis, Azure, Cloudflare, managed storage, external AI or payment method is required for this AppHost. No self-hosted or production runtime needs Aspire.

## Alternative without Aspire
```sh
dotnet run --project src/FamilyTree.Api/FamilyTree.Api.csproj --urls http://127.0.0.1:5181
dotnet run --project src/FamilyTree.Web/FamilyTree.Web.csproj --urls http://127.0.0.1:5182
```
Run those two commands in separate terminal sessions. OTLP stays off in ordinary `dotnet run` unless explicitly enabled.

## Verification and caveats
```sh
dotnet restore FamilyTree.slnx
dotnet build FamilyTree.slnx -c Release
bash scripts/smoke-http.sh
bash scripts/smoke-aspire.sh
```
The Aspire smoke starts the actual AppHost and checks both HTTP health responses. It does **not** inspect the dashboard, prove service discovery, cross-service correlation, OTLP delivery, or exercise all failure paths. A passing CI process smoke is not dashboard evidence.

On Windows, from the repository root in PowerShell, start the AppHost with the single command above. Open the dashboard using the local link printed by Aspire (do not share its authentication token). Confirm both resources are healthy, then in a second PowerShell window make synthetic requests:

```powershell
Invoke-WebRequest http://127.0.0.1:5181/health
Invoke-WebRequest http://127.0.0.1:5181/api/v1/capabilities
Invoke-WebRequest http://127.0.0.1:5182/health
```

In the dashboard inspect both resources' traces and `familytree.http.requests` / `familytree.http.duration` metrics after allowing time for export. Check that tags contain only the documented bounded operation/result values, and no paths, query strings, headers, identities, names or payloads. Do not copy dashboard URLs, logs or raw telemetry into public evidence. Report only the tested configuration and pass/fail results. The Web resource has an Aspire reference to API but currently makes no API call, so these requests do not demonstrate cross-service discovery or correlation.

Issue [#41](https://github.com/Jamula/FamilyTree/issues/41) remains open: end-to-end dashboard/export verification, unreachable-exporter behavior, privacy review and coverage targets require evidence; real authorized GEDCOM, identity, moderation and AI workflows must not be claimed before they exist. Managed telemetry remains subject to reviewed sampling, retention, quotas, TLS, scrubbers and explicit $0 owner approval.

### Linux verification (2026-11-24, PR #116)
The actual AppHost reported API and Web healthy through `aspire wait`, and both HTTP health requests succeeded. The sandbox's HTTPS development certificates were not trusted. The dashboard telemetry API returned no spans, both with its default HTTPS OTLP endpoint and with a temporary loopback-only HTTP OTLP endpoint; dashboard delivery remains **unverified**, not a pass. No Windows dashboard inspection was performed.

Separately, the assertion suite sent synthetic requests through the Web middleware to the real OTLP/HTTP exporter and a temporary loopback test receiver. It verified trace and metric payloads, bounded result labels, absence of synthetic sensitive markers, and that requests still work after the receiver stops. Existing configuration tests cover disabled and malformed exporters; these tests do not establish dashboard delivery, gRPC behavior, process-log scrubbing, or private API behavior.

The local CI-equivalent `dotnet-coverage` run measured source line coverage: Domain 96.2%, Persistence 97.6%, API 96.9%, Web 96.5%, MCP 97.7%, ServiceDefaults 100%, SyntheticDemo 100%. The collector reported 77.1% aggregate branches across instrumented assemblies, including tests, and supplied no per-component branch conditions; the 90% per-component branch target and AppHost coverage remain unverified. These measurements precede integration with [#107](https://github.com/Jamula/FamilyTree/pull/107) and [#108](https://github.com/Jamula/FamilyTree/pull/108); both modify shared application/test composition and require a fresh integrated privacy and coverage run.

## Zero-cost and privacy gates
Use only localhost, with only synthetic people. Aspire does not authorize provisioning cloud services. Never `aspire publish` or deploy without a separately reviewed $0 plan and owner approval. No family data may be inserted into public logs, console output or telemetry.
