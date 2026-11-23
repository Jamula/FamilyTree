# Privacy-safe observability foundation (#22)

This project uses native .NET diagnostics (ActivitySource and Meter) with an intentionally tiny, allowlisted tag vocabulary. The OpenTelemetry 1.21 SDK and OTLP exporter are opt-in, disabled by default; see [local setup](otel_local.md).

## Current instrumentation
- Activity source and meter: `FamilyTree.Api`
- Activity: `familytree.http`
- Counter: `familytree.http.requests`
- Histogram: `familytree.http.duration` (ms)
- Permitted labels only: `familytree.operation` (health, capabilities, genealogy, other), `familytree.result` (success, client_error, server_error)
- No identifier or caller-supplied attribute can be included through this API.
- No OTLP exporter is created unless explicitly enabled with a valid endpoint. Non-local endpoints require a separate privacy and cost approval flag.

## Bounded workload hooks
`FamilyTelemetry.RecordWorkflow` has enums for authorization, graph traversal, GEDCOM import/export, moderation and AI requests, and reports bounded allowed/denied/failed counts and durations. `RecordAiTokens` records only numeric token usage. **These are instrumentation hooks, not a claim that incomplete workflows already exist**. M1/M2 feature implementations must call them from trusted API boundaries after permissions are enforced.

## Forbidden information
Names, email addresses, account/family/person IDs, birth dates, GEDCOM contents, full URLs, request paths containing identifiers, query strings, photos, tokens, cookies, prompts, message bodies, IP addresses and exception detail strings.

## Future work
1. Extend safe, bounded metrics and local Aspire Dashboard integration in #23.
2. Wire Aspire Dashboard for **local-only** inspection (#23).
3. Add trace-context propagation, sampling and retention policy, backend-specific adapters, and privacy audits.
4. Add CI privacy tests for accidental instrument attributes and a safe scrape/proxy endpoint, if required.

All self-hosted environments must be able to turn telemetry off. Hosted integrations and any billable exporters require explicit cost approval (#25).
