# Test strategy and release quality gates

## Current measured source coverage

CI instruments the real .NET 11 test process using Cobertura. The **overall**
coverage is the instrumenter's root percentage and may include test code.
The separately calculated **per-component** source-line and branch coverage
excludes test and generated files. The M1 architectural target requires at
least **95% measured line and 90% measured branch coverage for each** of:
`FamilyTree.Domain`, `FamilyTree.Persistence`, `FamilyTree.Api`, `FamilyTree.Web`,
`FamilyTree.Mcp`, `FamilyTree.ServiceDefaults`, and
`FamilyTree.SyntheticDemo`. Missing or unmeasured components fail this
gate rather than counting as covered. A component with no branches requires
an explicit allowlist justification; it is never counted as 100%. The
overall M1 target is also 95% lines and 90% branches.

The latest local baseline from the real .NET 11 test process (the same
`dotnet-coverage` Cobertura collector used by CI) is:

| Scope | Lines | Branches | Conditions |
|---|---:|---:|---:|
| Overall instrumented report | 98.3% | 77.0% | 3,820 |
| Domain | 96.2% | 77.0% | 1,609 |
| Persistence | 97.6% | 83.8% | 308 |
| Api | 96.9% | 93.7% | 158 |
| Web | 96.8% | 88.6% | 273 |
| Mcp | 97.7% | 100.0% | 58 |
| ServiceDefaults | 95.1% | 100.0% | 68 |
| SyntheticDemo | 100.0% | 100.0% | 20 |

This baseline is evidence for the remaining exception-path work; it does not
claim that the 95/90 target currently passes. The collector emits real
per-line Cobertura condition counts, including `True`/`False` branch flags.
The blocking transition gate remains 90% lines while the target steps report
95% lines and 90% branches without masking their failures. Every new PR and
`main` push must rerun the coverage check. **These are measured test
observations, not a claim that all user features work.**

The compact exception-path matrix should prioritize validation, authentication
failure, privacy denial, malformed input, corruption, concurrency/rollback,
cancellation, recovery, telemetry and dependency failures. It must assert
outcomes, reuse synthetic fixtures, and report scenario count and runtime.
The current added matrix reports 7 scenarios; the full executable assertion
suite measured 10.32 seconds locally. Final target acceptance requires the
staged target steps to pass, followed by making them blocking.

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
