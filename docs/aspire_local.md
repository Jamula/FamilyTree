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

## Zero-cost and privacy gates
Use only localhost, with only synthetic people. Aspire does not authorize provisioning cloud services. Never `aspire publish` or deploy without a separately reviewed $0 plan and owner approval. No family data may be inserted into public logs, console output or telemetry.
