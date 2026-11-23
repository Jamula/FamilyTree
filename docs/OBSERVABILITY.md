# Privacy-safe observability foundation (#22)

This PR introduces **native .NET diagnostics primitives** (ActivitySource and Meter) with an intentionally tiny, allowlisted tag vocabulary. They can later feed OpenTelemetry .NET and OTLP exporters.

## Current instrumentation
- Activity source and meter: `FamilyTree.Api`
- Activity: `familytree.http`
- Counter: `familytree.http.requests`
- Histogram: `familytree.http.duration` (ms)
- Permitted labels only: `familytree.operation` (health, capabilities, genealogy, other), `familytree.result` (success, client_error, server_error)
- No identifier or caller-supplied attribute can be included through this API.
- No external exporter is configured. No telemetry leaves the running app from these new instruments.

## Forbidden information
Names, email addresses, account/family/person IDs, birth dates, GEDCOM contents, full URLs, request paths containing identifiers, query strings, photos, tokens, cookies, prompts, message bodies, IP addresses and exception detail strings.

## Future work
1. Add supported OpenTelemetry .NET packages and a configurable, opt-in OTLP exporter after dependency/security review.
2. Wire Aspire Dashboard for **local-only** inspection (#23).
3. Add trace-context propagation, sampling and retention policy, backend-specific adapters, and privacy audits.
4. Add CI privacy tests for accidental instrument attributes and a safe scrape/proxy endpoint, if required.

All self-hosted environments must be able to turn telemetry off. Hosted integrations and any billable exporters require explicit cost approval (#25).
