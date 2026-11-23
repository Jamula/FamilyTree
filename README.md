# FamilyTree

A privacy-first, standards-based family heritage platform, distinct from Family Hub.

## Product vision
Preserve family history, connect generations, celebrate people, and let families control their data.

## Proposed features
- GEDCOM 7 import/export and person-centered tree navigation
- Adjustable generations, spouses, sibling branches, and optional social connections
- Opt-in participation, profile claims, and administrator-approved changes
- Ring-fenced privacy by family, person, field, event, and photo
- Life events, media, gratitude messages, and family research
- Provider-neutral AI assistance with permission-aware retrieval
- Self-hosted open-source and hosted freemium editions

## Technology direction
.NET / Blazor first, OpenID Connect, documented APIs, portable genealogy records. The genealogy engine and hosting model remain under evaluation.

## Privacy requirement
**This is a public code repository. Never commit real relatives' records, private photos, birth/death dates, email addresses, phone numbers, genealogy exports, secrets, or production configuration.** Development fixtures must be synthetic. Privacy must be enforced on the backend, not only by UI filtering.

## Status
M0 engineering foundations are merged: .NET 11 API/web, SHA-pinned CI and smoke checks, protected graph projection, synthetic GEDCOM fixture, local Aspire AppHost and opt-in telemetry. The user-facing private family MVP remains M1 work. No real family information is deployed publicly.

## Development baseline
- **Framework:** .NET 11 GA (`net11.0`), SDK `11.0.100` or compatible .NET 11 servicing patch.
- **Product version:** independent of framework; the intended initial product release is `0.1.0`.
- **Cost policy:** $0 incremental **hosting/production infrastructure**. GitHub and other development tooling is shared engineering overhead allocated across active projects; new paid services/overages require specific owner approval. See [Cost policy](docs/COST_POLICY.md).
- **Local setup:** [Development instructions](docs/DEVELOPMENT.md).
