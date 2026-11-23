# Aspire 17 local development, no cloud charges (#23)

Aspire 17 provides a local developer dashboard and project orchestration. This project uses **free .NET 11**, **Aspire.Hosting.AppHost 17.0.0** and **Aspire.Hosting.Dotnet 17.0.0**. In 17.0 the modern supported API is `AddDotnetProject`; legacy `AddProject` is obsolete.

## Single command (Windows, macOS or Linux)
```sh
dotnet run --project src/FamilyTree.AppHost/FamilyTree.AppHost.csproj
```
The AppHost resolves the matching free Aspire CLI with DNX. If the CLI is already installed, `aspire run` is also supported; see [Aspire setup](https://aspire.dev/get-started/aspire-sdk/).

AppHost coordinates:
- API on localhost port 5181, `/health` and `/api/v1/capabilities`.
- Web on localhost port 5182, public landing page.
- Aspire local Dashboard for resource status, traces and metrics.

Only **synthetic** requests are permitted; no production family data or secrets. Private genealogy API endpoints deliberately respond HTTP 501 until membership and branch privacy are fully implemented.

## Observability
The API uses native `FamilyTelemetry` tracing and metrics plus an opt-in local OTLP exporter (tracked in #22). This AppHost opts in via `FAMILYTREE_OTLP_ENABLED=true` and uses Aspire's loopback OTLP service configuration. If the dashboard does not show telemetry, verify its localhost OTLP endpoint is present; never substitute a remote vendor endpoint without explicit approval.

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
```
The CI build checks that the AppHost compiles. The HTTP smoke tests exercise the apps independently, not a full Aspire-dashboard end-to-end orchestration. A developer must verify dashboard traces and resource health locally before #23 is declared complete.

## Zero-cost and privacy gates
Use only localhost, with only synthetic people. Aspire does not authorize provisioning cloud services. Never `aspire publish` or deploy without a separately reviewed $0 plan and owner approval. No family data may be inserted into public logs, console output or telemetry.
