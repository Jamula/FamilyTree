## Purpose and linked issue
<!-- Link a GitHub issue and explain the user-facing behavior or acceptance gate. -->

## Ownership and dependencies
<!-- Name one owner/agent and the independent file or contract lane. Link prerequisites or coordinated PRs; check for existing ownership before starting overlapping work. -->

## Work and review handoff
<!-- Keep WIP work in draft and batch local fixes before pushing. Triage findings together against the current head; note completion/blocker, evidence and next owner. No hourly status reports. -->

## Evidence
- [ ] Build, tests and relevant synthetic-data scenarios pass.
- [ ] No secrets, real family records, private images, genealogy exports or PII added.
- [ ] Authorization reviewed for every new API, graph, search, media or export operation.
- [ ] No Azure/Cloudflare/AI/paid service enabled, and no new spend without explicit owner approval.
- [ ] Dependencies and licenses reviewed; GitHub Actions are pinned to full immutable SHAs.
- [ ] Documentation/requirements traceability updated where applicable.
- [ ] For auth, persistence, or CI changes, independent security review is complete and all review findings are resolved or evidence-backed.

## Rollback and release notes
<!-- Describe rollback plan and user-visible change, or say not applicable. -->
