# Requirements-to-Issues Traceability

Every P0 requirement must become an executable test before release. The following links are a starting map, **not a claim of implementation**.

| Requirement | Issue | Acceptance gestures / tests | Status |
|---|---|---|---|
| Secure CI and policy | [#6](https://github.com/Jamula/FamilyTree/issues/6) | Restore/build, pinned action SHA, required checks | In progress |
| Open-source governance | [#7](https://github.com/Jamula/FamilyTree/issues/7) | Security reporting, licensing, clean public fixtures | In progress |
| Acceptance test matrix | [#8](https://github.com/Jamula/FamilyTree/issues/8) | Browser, integration and unit test coverage | In progress |
| Identity and opt-in | [#9](https://github.com/Jamula/FamilyTree/issues/9) | U01–U06, U20, U33 | Planned |
| Branch / tenant privacy | [#10](https://github.com/Jamula/FamilyTree/issues/10) | U04, U07, U19, U23, U26, U33 | Planned |
| Person-centered tree | [#11](https://github.com/Jamula/FamilyTree/issues/11) | U08–U14, U31–U32 | Planned |
| Unconnected persons and relationships | [#12](https://github.com/Jamula/FamilyTree/issues/12) | U15–U20 | Planned |
| GEDCOM 7 exchange | [#13](https://github.com/Jamula/FamilyTree/issues/13) | U22–U23, U29 | Planned |
| Gratitude, media, feedback | [#14](https://github.com/Jamula/FamilyTree/issues/14) | U21, U24–U25, U28 | Planned |
| Optional AI copilot | [#15](https://github.com/Jamula/FamilyTree/issues/15) | U26–U27 | Planned |
| Self-hosted and hosted freemium | [#16](https://github.com/Jamula/FamilyTree/issues/16) | U29–U30, tenant isolation | Planned |
| PWA / mobile / sponsorship | [#17](https://github.com/Jamula/FamilyTree/issues/17) | U31–U32, installation tests | Planned |

| Observability and safe instrumentation | [#22](https://github.com/Jamula/FamilyTree/issues/22) | T01–T04, no private tags or external export by default | In progress |
| Aspire local developer stack | [#23](https://github.com/Jamula/FamilyTree/issues/23) | T05, AppHost build and local startup | In progress |
| Zero-cost approval | [#25](https://github.com/Jamula/FamilyTree/issues/25) | C01, workflow guard without paid cloud resources | In progress |
| Protected auto-merge | [#26](https://github.com/Jamula/FamilyTree/issues/26) | C02, required CI + squash + review resolution | In progress |

## Executable M0 tests
- T01: `TelemetryAssertions` ensures sanitized trace/meter vocabulary.
- T02: `TreeProjectionAssertions` ensures hidden nodes/edges are removed.
- T03: GEDCOM synthetic fixture smoke tests, not GEDCOM standards conformance.
- T04: `scripts/smoke-http.sh` checks health, landing page and HTTP 501 fail-closed routes.
- C01: `scripts/verify-zero-cost.py` enforces source-level action pinning and no configured deployments.
- C02: GitHub `build-test` is the required branch rule check.

| MCP tool gateway and safe agent discovery | [#44](https://github.com/Jamula/FamilyTree/issues/44) | Synthetic stdio handshake, bounded read tools, no approved writes | In progress |
| Fictional multi-generation demo data | [#45](https://github.com/Jamula/FamilyTree/issues/45) | 32 fictional people, deterministic IDs, all relationships valid | In progress |
| Agent proposals and real-data approval | [#46](https://github.com/Jamula/FamilyTree/issues/46) | No production access or automatic publication until M1 policies tested | Planned |

## Engineering acceptance
- Each pull request references its issue and adds tests, or documents why automated coverage is not appropriate.
- All read paths and graph traversal tests demonstrate *no existence leaks* for unauthorized people.
- Integration and E2E fixtures contain only synthetic data.
- No release with actual family records until auth, privacy and auditing gates are met.
