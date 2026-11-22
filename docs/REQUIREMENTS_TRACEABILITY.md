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

## Engineering acceptance
- Each pull request references its issue and adds tests, or documents why automated coverage is not appropriate.
- All read paths and graph traversal tests demonstrate *no existence leaks* for unauthorized people.
- Integration and E2E fixtures contain only synthetic data.
- No release with actual family records until auth, privacy and auditing gates are met.
