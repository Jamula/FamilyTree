# Test Strategy

## Current coverage
The existing console-based domain assertions test tree defaults, independent relationships, invalid self-links and private-default gratitude. They are a starting point; they do not satisfy the PRD's full acceptance matrix.

## Next executable suites
1. **Domain unit tests:** relationship semantics, date uncertainty, graph constraints, export semantics.
2. **API integration tests:** anonymous access denied, branch/tenant isolation, nonenumeration, authenticated exports and media.
3. **Browser gesture tests:** pointer and keyboard recentering; 0–8 sliders; spouse/sibling/social toggles; invitation and contribution workflows.
4. **GEDCOM corpus tests:** valid 7.0 roundtrip, 5.5.1 import, ambiguous dates, associations, duplicates and loss reporting.
5. **Security and privacy tests:** hidden people not leaked through counts, search, nodes, edges, AI output, logs, export or cached pages.

## CI gates
Build and domain assertions are configured; executable API integration and browser suites are not yet built. Add them on their feature branches and make them required before real-data deployments.

## Current architecture restriction
Avoid treating the placeholder `501` API responses as authorized data implementations. Production data must remain private until P0 access policies are enforced.
