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
API and Web use native `FamilyTelemetry` tracing and metrics plus the opt-in OTLP exporter (tracked in #22). This AppHost opts both in via `FAMILYTREE_OTLP_ENABLED=true` and uses Aspire's loopback OTLP service configuration. If the dashboard does not show telemetry, verify its localhost OTLP endpoint is present; never substitute a remote vendor endpoint without explicit approval. The opt-in tracer samples all allowed custom spans, including those under an unrecorded parent; framework request traces and application logs are not configured for OTLP export. Aspire can separately display process output, which is not guaranteed to be scrubbed; use synthetic data only.

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

Issue [#41](https://github.com/Jamula/FamilyTree/issues/41) remains open: dashboard metrics, integrated failure/privacy paths, cross-service correlation and coverage targets require evidence; real authorized GEDCOM, identity, moderation and AI workflows must not be claimed before they exist. Managed telemetry remains subject to reviewed sampling, retention, quotas, TLS, scrubbers and explicit $0 owner approval.

### Linux verification (2026-11-24, PR #116)
On code commit `49476d0`, the actual Debug AppHost reported API and Web healthy through `aspire wait`, and both HTTP health requests succeeded. After synthetic health requests, the dashboard telemetry API returned six API and four Web `familytree.http` spans; their only attribute key was `familytree.operation`, and a synthetic query marker was absent. The sandbox reported partially untrusted HTTPS development certificates. This verifies dashboard trace delivery for those requests, not dashboard metrics or every possible field and scenario. No Windows dashboard inspection was performed.

Separately, the assertion suite sent synthetic requests through Web middleware with normal logging enabled to the real OTLP/HTTP exporter and a temporary loopback test receiver. It checked an unrecorded server-side parent, signal-specific paths, trace and metric payloads for expected result strings and absence of synthetic sensitive markers, and that requests still work after a rejected export and after the receiver stops. It does not decode and validate every serialized attribute. Existing configuration tests cover disabled and malformed exporters; these tests do not establish dashboard metric delivery, integrated gRPC failure behavior, process-log scrubbing, or private API behavior.

The local `dotnet-coverage` run on `49476d0` reported assembly line/branch rates: Domain 96.14%/77.07%, Persistence 97.60%/83.77%, API 96.01%/93.67%, Web 96.18%/88.73%, MCP 97.78%/100%, ServiceDefaults 100%/97.22%, and SyntheticDemo 100%/100%. These assembly rates can include generated code; AppHost was not measured. Aggregate branch coverage was 77.12%, including tests. Domain, Persistence and Web do not meet the 90% branch target in this measurement. These measurements precede integration with [#107](https://github.com/Jamula/FamilyTree/pull/107) and [#108](https://github.com/Jamula/FamilyTree/pull/108); both modify shared application/test composition and require a fresh integrated privacy and coverage run.

## Zero-cost and privacy gates
Use only localhost, with only synthetic people. Aspire does not authorize provisioning cloud services. Never `aspire publish` or deploy without a separately reviewed $0 plan and owner approval. No family data may be inserted into public logs, console output or telemetry.
