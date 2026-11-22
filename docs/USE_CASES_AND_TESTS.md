# Use Cases and Gesture-Level Acceptance Tests

Each case is evaluated under viewer, member, steward, administrator and unauthorized sessions where applicable. Automated browser tests should cover keyboard, pointer and touch semantics.

| ID | Gesture / use case | Expected behavior and test assertion |
|---|---|---|
| U01 | Click invitation link | Valid invitation opens consent screen; invalid/expired link reveals no personal data |
| U02 | Opt in to membership | Records requested state; family administrator must approve |
| U03 | Decline invitation | No membership created; existing historical record unaffected |
| U04 | Join a family branch | User can access only explicitly granted branch records |
| U05 | Claim own person profile | Pending, evidence reviewed, cannot approve own claim |
| U06 | Pause/leave | Access token and grants invalidated; person record retained according to privacy policy |
| U07 | Search person | Hidden people absent from results and counts |
| U08 | Select center person | Recenter tree to selected authorized person |
| U09 | Slide ancestor depth 0–8 | Ancestor node depth changes; unauthorized ancestors never appear |
| U10 | Slide descendant depth 0–8 | Descendant depth changes with correct clipping |
| U11 | Toggle spouses | Show/hide authorized spouse/partner links |
| U12 | Toggle sibling branches | Show/hide authorized collateral branches |
| U13 | Toggle social connections | Default off; show authorized friends/neighbors/mentors when enabled |
| U14 | Click any visible node | Recenter, preserving chosen toggles |
| U15 | Add unconnected person | Draft record pending review, no auto-join |
| U16 | Propose relationship | Existing two people can have multiple distinct links with stable IDs |
| U17 | Custom relationship label | Validate label, provenance, dates and moderation |
| U18 | Suggest edit | Show before/after diff; pending until approved |
| U19 | Steward approves | Only authorized branch changes applied; action audited |
| U20 | Attempt self approval | Server rejects |
| U21 | Upload photo | Consent, antivirus/type/size checks, protected media access |
| U22 | Import GEDCOM | Preview, duplicates, sources, human confirmation; no automatic public release |
| U23 | Export GEDCOM | Authorized records only; private fields and hidden links excluded |
| U24 | Send gratitude | Recipient-private by default, sender/recipient validated |
| U25 | Request broad gratitude sharing | Requires recipient consent and applicable moderation |
| U26 | Ask AI about family | Retrieval limited to same permissions as ordinary API; citations shown |
| U27 | AI proposes changes | Human reviews and explicitly approves; no direct mutation |
| U28 | Submit feedback | Sanitization, abuse limits, receipt/triage ID, no private data in public GitHub |
| U29 | Request data portability | Download authorized machine-readable records and media |
| U30 | Request erasure | Verified request processed with records/third-party rights reviewed |
| U31 | Open mobile viewport | Buttons reachable, responsive layout, assistive-tech labels |
| U32 | Navigate with keyboard | Focus visible; toggles/selection/menus work without pointing device |
| U33 | Switch family | Isolation test: no records, cache or AI context leak between families |

## Automated test layers
- Unit: relationships, dates, GEDCOM mapping, access-decision matrices and privacy defaults.
- Integration: authentication, API permissions, review workflows, tenant isolation, export and media.
- E2E: each gesture above with mouse, touch simulation, keyboard and screen reader checks.
- Security: unauthorized IDs return indistinguishable errors; hidden graph nodes and links never cross boundaries.
Current automated tests in this PR are *initial domain assertions*, not full coverage of this catalog.
