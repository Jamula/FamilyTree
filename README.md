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
Bootstrap only. Product documentation, app code, CI, and security tests will be added through reviewed pull requests. No family information is deployed publicly.
