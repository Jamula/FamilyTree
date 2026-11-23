# Architecture and Extensibility Decisions

- ADR-001: Independent codebase and product separate from Family Hub.
- ADR-002: Standard GEDCOM 7 exchange, with validated round-trip tests; extension data in namespaced fields/sidecar export.
- ADR-003: .NET 11 GA domain, server APIs, Blazor UI; no dependency on Gramps/webtrees for authoritative data. Evaluate mature libraries for import/export and visualization rather than reimplementing algorithms.
- ADR-004: Deny by default at server and query boundary, including family/branch/person/field/media scopes; no public living-person directory.
- ADR-005: People are distinct from user accounts. Membership opt-in is not required for genealogical representation.
- ADR-006: Multiple independent relationship edges per pair; distinguish parent, spouse, partner, sibling and social associations.
- ADR-007: Authentication OIDC; claims and moderation in dedicated workflow modules; audit everything.
- ADR-008: Provider-neutral AI via IChatCompletionProvider and tool interfaces; permission-aware retrieval, citations, no silent data writes, optional local model; disabled by default until privacy controls tested.
- ADR-009: One codebase for self-hosted and hosted freemium; extension points via versioned API, domain events, import/export adapters and configurable storage providers.
- ADR-010: Gramps Web and webtrees are inspiration/compatibility targets, not runtime dependencies. Verify third-party licenses before reusing source or UI components.

## Proposed layers
Web: Blazor experience; API: authorized HTTP access and OpenAPI; Domain: genealogy models; Infrastructure: storage, identity, GEDCOM adapter, media, events, AI provider adapters.

## Compliance/feedback requirements
GDPR lawful basis, rights request procedures, data minimization, access/export/erasure, incident response, retention schedules, minors and jurisdiction-specific regulations. In-app feedback must strip PII before any public GitHub issue is created. Global family discovery requires explicit mutual consent. Product release requires qualified legal/privacy review.

## M0 concrete platform decisions
See [M0 architecture decisions](ARCHITECTURE_DECISIONS_M0.md) for local SQLite, protected local media, provider-neutral OIDC/AI, local Aspire, and disabled-by-default OTLP exporters. Production database and managed hosting require separate privacy/security and $0 cost approval gates.
