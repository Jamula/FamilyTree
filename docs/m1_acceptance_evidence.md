# M1 local acceptance evidence manifest

This manifest tracks executable acceptance evidence for #90. The harness is
synthetic-only, starts the actual API in a separate local process, and tests
only routes and responses that exist today. A green harness or CI run does not
mean that the private-family MVP has passed.

## Reproduce the current baseline

Requirements: .NET 11 SDK, Git, and a local terminal. No paid services,
provider credentials, private records, or family-data database are required.

From the repository root on Windows PowerShell:

```powershell
dotnet --version
dotnet restore FamilyTree.slnx
dotnet build FamilyTree.slnx --configuration Release --no-restore
dotnet run --project tests/FamilyTree.AcceptanceHarness/FamilyTree.AcceptanceHarness.csproj --configuration Release
```

On Linux or macOS, run the same `dotnet` commands from the repository root.
The harness clears inherited `FAMILYTREE_*`, `OTEL_*`, Kestrel, listener and
content-root settings, including host-prefixed and case-insensitive aliases. It
uses an empty temporary content root, forces OTLP off, binds only to loopback on a temporary
port, and terminates the child process when done. CI runs the real process with
hostile OIDC, private-mode, Kestrel, content-root and OTLP settings under
multiple aliases. The current private boundary is unconditional, not controlled
by a switch. The harness does not create valid authenticated sessions or write
family data.

Expected output includes `PASS` for local API process startup, health and
capabilities, default-disabled OIDC, anonymous/forged-session denials, and
uniform empty 501 responses for reserved private paths. It also prints
`UNACCEPTED` for unsupported gates; these are blockers, not passing tests.

## Evidence status

| Acceptance area | Status | Evidence / limitation |
| --- | --- | --- |
| Actual API process and HTTP startup | Verified locally and in CI | `tests/FamilyTree.AcceptanceHarness/Program.cs`; PR #103 run 45330602101 passed the process-level acceptance step. |
| Child-process telemetry isolation | Verified locally and in CI | The real API baseline passes with inherited `FAMILYTREE_OTLP_ENABLED=true`, remote approval, and an invalid OTLP endpoint; PR #103 run 45488727184 passes the hostile-environment harness step without changing #115's coverage gates. |
| Anonymous and malformed/forged session denial | Verified baseline | `/auth/session` returns 401 without a cookie and for invalid cookie values. |
| Valid expired or revoked session-cookie denial | **Unaccepted** | A real trusted session and revocation path are not available; invalid cookie text is not an expired/revoked-session test. |
| Private GET/search/tree/export and write behavior | Verified fail-closed baseline only | Reserved paths and POST return uniform empty 501 responses; no handler is claimed to work. |
| Forbidden versus unknown record equivalence | **Unaccepted** | The current API has no authorized record handlers or seeded known record, so this harness only confirms reserved paths remain uniformly blocked. |
| Verified OIDC, membership/invitations, family/person CRUD, authorized tree, editing/moderation, GEDCOM, persistence/restart, backup/restore, browser workflow, Windows UI | **Unaccepted** | These integration behaviors are not yet wired into the live API workflow. |
| CI line-coverage gates | Verified for this changeset | PR #103 run 45330602101 measured 98.3% aggregate line coverage and passed every component's >=90% line threshold; aggregate branch coverage was 78.0%. Branch coverage by component was not measured. |
| Full negative privacy/error matrix | **Unaccepted** | Requires the integrated workflows and independent acceptance evidence; this baseline does not close #90 or constituent feature issues. |
| Owner demonstration and private-MVP approval | **Unaccepted** | No owner acceptance or version change is implied. Product version remains 0.1.0. |

## CI evidence snapshot

As of 2026-11-24, the latest observed `main` build/test workflow completed
successfully after #100 on commit [`407b319`](https://github.com/Jamula/FamilyTree/commit/407b319a205dab9273f26db4a56b0218014c1629)
([workflow run 45488006511](https://github.com/Jamula/FamilyTree/actions/runs/45488006511)).
This integration branch's build/test workflow also passed on commit
[`46a69bf`](https://github.com/Jamula/FamilyTree/commit/46a69bfdabb86c55e38727772d0efb7f91b106ce)
([PR #103 run 45330602101](https://github.com/Jamula/FamilyTree/actions/runs/45330602101)),
including the new process-level acceptance step. That run reported 98.3%
aggregate line coverage, 78.0% aggregate branch coverage, and component line
coverage above 90% for each measured component. These green CI results are not
proof that the private-family MVP is complete; the remaining gates above stay
unaccepted.

After merging #100 into this branch, PR #103 build/test
([run 45488727184](https://github.com/Jamula/FamilyTree/actions/runs/45488727184))
passed on commit `090904e`, including the hostile inherited-OTLP regression.
Independent review found no significant issue; local CodeQL reported zero
Actions and C# alerts. The local full-solution build was blocked by the
uninstalled Aspire CLI bundle (`ASPIRE009`); the targeted API/harness build,
both real-process baselines, and the existing .NET assertion suite passed.

## Expansion rule

Add a gate only when a live, reviewed API behavior exists. Use actual trusted
identity and persisted synthetic fixtures; do not mock authentication or claim
missing routes work. Keep this manifest's unaccepted entries until their
end-to-end behavior, denial cases, restart behavior, and independent review
have evidence. Do not remove `PrivateApiBoundary` or close #90 as part of this
baseline harness.
