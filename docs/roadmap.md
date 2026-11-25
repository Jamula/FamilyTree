# FamilyTree roadmap

This roadmap is the canonical reference for milestone numbering, issue grouping and provisional dates. Issue numbers and acceptance criteria remain stable; milestone prefixes in issue titles are grouping labels, not substitutes for those records.

## Canonical milestone identifiers

| Current identifier | Legacy identifier | Name | Status |
|---|---|---|---|
| M01 | M0 | Foundation | Complete; the exact completion date has not been independently verified |
| M02 | M1 | Private Family MVP | Current; acceptance remains open |
| M03 | M2 | Collaboration and AI | Planned |
| M04 | M3 | Mobile and expansion | Planned |
| M05 onward | — | Future milestones | Not yet scoped |

Use two-digit milestone IDs beginning at `M01`. Optional sub-milestones use two digits after the period, for example `M02.01`. When updating text that contains multiple legacy IDs, map each token simultaneously: M0→M01, M1→M02, M2→M03 and M3→M04. Do not cascade replacements (for example, M1 must not become M03).

## M02 · Private Family MVP

Issue states below reflect GitHub as of 2026-11-24. “Open” describes the issue record only; it is not evidence that implementation is ready or accepted.

| Sub-milestone | Workstream and stable issue links | Status | Provisional target (Pacific time) | Confidence | Blocking dependencies | Change since previous forecast |
|---|---|---|---|---|---|---|
| M02.01 | Identity, authentication and privacy: [#9](https://github.com/Jamula/FamilyTree/issues/9), [#10](https://github.com/Jamula/FamilyTree/issues/10), [#105](https://github.com/Jamula/FamilyTree/issues/105), [#110](https://github.com/Jamula/FamilyTree/issues/110) | Issues open | Nov 25–26, 2026 | Low | Identity-provider and local verification; owner acceptance | Initial planning baseline; no earlier forecast recorded |
| M02.02 | Genealogy, relationships and UI: [#11](https://github.com/Jamula/FamilyTree/issues/11), [#12](https://github.com/Jamula/FamilyTree/issues/12), [#106](https://github.com/Jamula/FamilyTree/issues/106) | Issues open | Nov 25–27, 2026 | Low | TBD; end-to-end sequencing and live implementation dependencies need confirmation | Initial planning baseline; no earlier forecast recorded |
| M02.03 | GEDCOM: [#13](https://github.com/Jamula/FamilyTree/issues/13) | Issue open | Nov 26–27, 2026 | Low | TBD; authorized import/export sequencing and acceptance evidence need confirmation | Initial planning baseline; no earlier forecast recorded |
| M02.04 | Quality, recovery and coverage: [#67](https://github.com/Jamula/FamilyTree/issues/67), [#109](https://github.com/Jamula/FamilyTree/issues/109), [#114](https://github.com/Jamula/FamilyTree/issues/114) | Issues open | Nov 26–28, 2026 | Low | 95% line / 90% branch quality targets; active [coverage PR #115](https://github.com/Jamula/FamilyTree/pull/115) | Initial planning baseline; no earlier forecast recorded |
| M02.05 | End-to-end acceptance: [#90](https://github.com/Jamula/FamilyTree/issues/90) | Issue open | Nov 26–28, 2026 | Low | Identity-provider/local verification and the 95% line / 90% branch quality targets | Initial planning baseline; no earlier forecast recorded |
| M02 | Private Family MVP parent milestone | In progress; acceptance not approved | Nov 26–28, 2026 | Low | Identity-provider/local verification and the 95% line / 90% branch quality targets | Initial planning baseline; no earlier forecast recorded |

The dates are low-confidence planning targets, not commitments. Refresh them from current PR, CI, measured coverage and acceptance evidence. Record actual finish dates only when independently verified; owner approval is separate from implementation readiness. Do not change the existing scope, priorities, acceptance criteria, security policy, 95/90 targets or CI to meet a forecast.

## Later milestones and cross-milestone work

| Identifier | Scope and stable issue links | Status | Target | Confidence | Blocking dependencies | Change since previous forecast |
|---|---|---|---|---|---|---|
| M03 | Collaboration and AI: [#14](https://github.com/Jamula/FamilyTree/issues/14), [#15](https://github.com/Jamula/FamilyTree/issues/15), [#16](https://github.com/Jamula/FamilyTree/issues/16) | Planned; issues open | TBD pending M02 completion and scoped planning | Not assessed | M02 completion and approved M03 scope/dependencies | Initial baseline; no earlier forecast recorded |
| M04 | Mobile and expansion: [#17](https://github.com/Jamula/FamilyTree/issues/17) | Planned; issue open | TBD pending M02 completion and scoped planning | Not assessed | M02 completion and approved M04 scope/dependencies | Initial baseline; no earlier forecast recorded |
| M05 onward | Future work | Not scoped | TBD; scope is not yet understood | Not assessed | Future planning | Initial baseline; no earlier forecast recorded |
| M02/M03 cross-cutting | Local telemetry and agent proposal/security gates: [#41](https://github.com/Jamula/FamilyTree/issues/41), [#46](https://github.com/Jamula/FamilyTree/issues/46) | Issues open | TBD; individual sequencing is not yet verified | Not assessed | Confirm each issue’s scope and dependencies during M02/M03 planning | Initial baseline; no earlier forecast recorded |

## GitHub tracking and release policy

The roadmap and issue-title prefixes document logical grouping. Native GitHub milestone records are separate from issue-title prefixes; the available repository connector does not expose milestone creation, so this document does not claim that native milestone records were created or updated.

Milestone completion does not publish or version the product. Product version remains **0.1.0 until the owner accepts the M02 Private Family MVP and separately approves a version change**. See [release readiness](release_readiness.md), [versioning](versioning.md) and [requirements traceability](requirements_traceability.md).
