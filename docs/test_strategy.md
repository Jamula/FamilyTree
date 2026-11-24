# Test strategy and release quality gates

## Current measured source coverage

CI instruments the real .NET 11 test process using Cobertura. The **overall**
coverage is the instrumenter's root percentage and may include test code.
The separately calculated **per-component** source-line coverage excludes
test and generated files. The architectural gate requires at least
**90% measured line coverage for each** of:
`FamilyTree.Domain`, `FamilyTree.Persistence`, `FamilyTree.Api`, `FamilyTree.Web`,
`FamilyTree.Mcp`, `FamilyTree.ServiceDefaults`, and
`FamilyTree.SyntheticDemo`. Missing or unmeasured components fail this
gate rather than counting as covered. The overall line-coverage floor
remains 90%.

The current baseline before enabling the six-area gate measured Domain
95.2%, Api 95.9%, Web 95.1%, Mcp 97.7% (from its tested feature branch),
ServiceDefaults 95.1%, and SyntheticDemo 100.0%.
Every new PR and `main` push must rerun the coverage check. **These are
measured test observations, not a claim that all user features work.**

Branch coverage is reported separately: the latest combined main build
reported 81.1% overall branch coverage. The instrumenter does not provide
reliable per-component branch conditions; the component reporter labels
these **Not measured**, rather than inventing estimates. We should continue
raising branch coverage with adversarial input, cancellation, concurrency,
authentication and error tests.

## Existing executable test categories

- Domain and workflow unit assertions: relationship semantics, family and
  branch filtering, field/edge privacy, GEDCOM subset limitations, duplicate
  and malformed inputs, review state transitions, undo and safe audit.
- API loopback integration: actual Kestrel application composition, anonymous
  session 401, default-off providers, forged identity headers, private
  read/write 501 and response body equality for denied/unknown IDs.
- SQLite integration: isolated synthetic databases, reopening and competing
  invitation acceptance through independent connections, pending-member denial,
  independent approval, rollback, audit and durable revocation. This exercises
  the persistence component directly, not authenticated HTTP or process restart.
- Web integration: fictional workbench GET/POST/antiforgery, malformed forms,
  approved/rejected transitions, stale versions, undo and localhost-only access.
- MCP: official stdio handshake, synthetic tool validation, bounded draft
  queue concurrency/overflow and host transport registration without starting
  a listener during unit tests.
- Telemetry: default-off exporter, invalid endpoint/config/enum handling,
  metric label privacy and failure/cancellation outcomes.
- Coverage reporter parser: missing or corrupted XML, duplicate source
  lines, independent branches, missing assemblies and hard-fail thresholds.

## Not yet complete (cannot claim M1 release readiness)

Real Google/Microsoft sign-in against registered providers, persistent
membership-to-principal mapping and revocation, fully authenticated
tenant/branch/person/field/media endpoints, durable reviewed genealogy
mutations, actual family tree, complete GEDCOM 7 and 5.5.1 interoperability,
mobile gestures and private data migration remain missing or incomplete.
Tests above are mostly synthetic and cannot substitute for those end-to-end
acceptance tests. Private family APIs stay fail-closed until the independent
security gates have passed. Product version remains 0.1.0.

Run the cross-platform executable assertions with
`dotnet run --project tests/FamilyTree.Tests/FamilyTree.Tests.csproj --configuration Release`.
The private MVP acceptance scenarios in [issue #90](https://github.com/Jamula/FamilyTree/issues/90)
remain unaccepted; a passing component or fail-closed boundary test is not a
passing login, browser, editing, import/export, or restart E2E scenario.
