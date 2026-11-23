# Fictional workbench, running locally (#11, #12)

This adds an interactive **fictional-only** end-to-end practice flow beside
the existing 32-person explorer. It is **not** the private-family MVP.

## Run on your PC

Install .NET 11 SDK, clone/pull the repository, then run:

```powershell
git pull
dotnet run --project src/FamilyTree.Web/FamilyTree.Web.csproj --urls http://127.0.0.1:5182
```

Open `http://127.0.0.1:5182/demo` and select
**Try local fictional editing and review**, or open
`http://127.0.0.1:5182/demo/workbench`.

The workbench is available only when the browser's connection is loopback
(`127.0.0.1` or `::1`). It has no free-text field and no connection to a
real-family database or the public demo's person IDs. It uses fixed invented
names plus generated `Fictional Guest N` labels and distinct fake actor IDs.

## Try a complete workflow

1. **Propose invented person**. It remains pending and invisible in the
   approved list.
2. **Approve pending change**. A simulated independent reviewer approves it.
3. **Propose friendship link**, then **Approve pending change**. The approved
   relationship count increases.
4. **Undo last approval**. The new link is removed and an append-only audit
   record marks the reversal.
5. Alternatively, **Reject pending change**. Rejected edits never appear in
   the approved graph.

The page uses keyboard-accessible HTML and CSRF token-validated POST actions.
It rejects invalid/duplicate transitions; every state-changing action uses
the reviewed-domain ledger rather than writing approved records directly.
HTTP smoke and unit tests cover these actions, invalid POSTs and undo.

## Explicit limitations

- Every edit is fictional, in-memory, shared only inside one local process.
  **No persistence**: restarting the web process resets everything.
- Simulated reviewers are **not authenticated people**. Real membership,
  OAuth/OIDC, tenant/branch/person permissions and server-side review
  authority are not implemented here.
- The local route must remain loopback-only and cannot accept private
  relatives' names, photos, dates, imported GEDCOM or user-supplied fields.
- The product's real-family `/api/v1` endpoints remain fail-closed at 501.
  Do not use the workbench to manage actual genealogy.
- Full M1 acceptance requires a durable, authorization-backed store, genuine
  moderator review, restored data and private authenticated tree UI.
