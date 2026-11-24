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
The harness clears inherited OIDC provider environment variables for its child
API process, binds only to loopback on a temporary port, and terminates the
child process when done. It does not create valid authenticated sessions or
write family data.

Expected output includes `PASS` for local API process startup, health and
capabilities, default-disabled OIDC, anonymous/forged-session denials, and
uniform empty 501 responses for reserved private paths. It also prints
`UNACCEPTED` for unsupported gates; these are blockers, not passing tests.

## Evidence status

| Acceptance area | Status | Evidence / limitation |
| --- | --- | --- |
| Actual API process and HTTP startup | Verified locally and in CI | `tests/FamilyTree.AcceptanceHarness/Program.cs`; PR #103 run 45330602101 passed the process-level acceptance step. |
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
successfully on commit [`bd8e67e`](https://github.com/Jamula/FamilyTree/commit/bd8e67e1271a311282563297c255d98348685267)
([workflow run 45330317828](https://github.com/Jamula/FamilyTree/actions/runs/45330317828)).
This integration branch's build/test workflow also passed on commit
[`46a69bf`](https://github.com/Jamula/FamilyTree/commit/46a69bfdabb86c55e38727772d0efb7f91b106ce)
([PR #103 run 45330602101](https://github.com/Jamula/FamilyTree/actions/runs/45330602101)),
including the new process-level acceptance step. That run reported 98.3%
aggregate line coverage, 78.0% aggregate branch coverage, and component line
coverage above 90% for each measured component. These green CI results are not
proof that the private-family MVP is complete; the remaining gates above stay
unaccepted.

## Expansion rule

Add a gate only when a live, reviewed API behavior exists. Use actual trusted
identity and persisted synthetic fixtures; do not mock authentication or claim
missing routes work. Keep this manifest's unaccepted entries until their
end-to-end behavior, denial cases, restart behavior, and independent review
have evidence. Do not remove `PrivateApiBoundary` or close #90 as part of this
baseline harness.
