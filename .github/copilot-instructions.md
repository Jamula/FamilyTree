# FamilyTree GitHub Copilot instructions

Follow [AGENTS.md](../AGENTS.md) for the complete rules.

**Before starting:** locate the relevant FamilyTree GitHub issue or create one. Every PR must link an issue and its acceptance tests. Reuse existing tracking issues rather than creating random duplicates. Update the issue with CI results, security limitations and follow-up work. Close only when accepted.

## Parallel work and reviews

- Split work into independent file or contract lanes. Name one owner/agent for each lane, record dependencies, and check open issues/PRs for existing ownership before dispatching or editing. Do not overlap edits or dispatch duplicate agents.
- Keep work-in-progress PRs in draft. Batch local fixes before pushing; move a PR out of draft when its scope and evidence are ready for review. Merge independent ready PRs incrementally rather than waiting for unrelated work.
- Triage review findings together against one exact PR head. Fix valid findings, document evidence for any non-actionable finding, then request final review on that exact head. Re-run review only when new commits materially change the reviewed head.
- On completion or a blocker, leave a short handoff with changed scope, verification evidence and dependencies or next owner. Do not send routine hourly status updates.
- For auth, persistence, or CI changes, preserve independent security review. Do not change repository or organization protections in code. Recommend disabling repository-level Copilot review-on-every-push only if an explicit final exact-head review can reliably be requested before merge; otherwise leave the setting enabled. Never change organization-wide rules.
- Coordinate CI changes with #114/#115 and their active owner before editing workflow or coverage files. Preserve the stable `build-test` check and never skip checks for draft PRs or docs-only changes. Per-PR superseded-run cancellation must not cancel main or release runs. Reuse Release outputs only when binaries are identical; remove restores/builds only after measuring exact-head equivalence. Preserve dependency review, all privacy/runtime tests, pinned Actions, and fail-closed source coverage gates, including missing-report failures. Measure before splitting jobs; consolidate coverage logic only when scopes, thresholds, and failure semantics match. Avoid extra artifacts or paid services.

Only synthetic test genealogies are allowed in this public repo. Never put actual Jamula family details, private photos, chats, dates, contact information or secrets into source, fixtures, reviews or telemetry.

Keep product version `0.1.0` until the private MVP passes and the owner approves a version change. Hosting must cost $0 incrementally; GitHub engineering costs are shared across projects, and new paid subscriptions still need explicit owner approval.

MCP tools may demonstrate fictional relationships and queue in-memory proposals but must not publish real genealogical information or bypass membership/authorization.
