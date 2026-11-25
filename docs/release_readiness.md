# M01 / 0.1.0 release checklist

M01 is the completed Foundation milestone (legacy M0); completion does not mean the release gates below or the M02 Private Family MVP are accepted. See the [roadmap](roadmap.md).

This is a pre-release foundation artifact. `0.1.0` cannot be published until every required release gate is complete or a specific exception is approved.

- [ ] All M01 issue acceptance criteria met and evidence linked; unresolved scope is documented and approved.
- [ ] SHA-pinned required CI build/test, domain assertions and executable HTTP smoke checks pass on the release commit.
- [ ] Dependency/security/license review is done; no known exposed secrets or actual genealogy records.
- [ ] Verify SECURITY.md private vulnerability reporting is enabled/tested.
- [ ] **$0 incremental hosting costs**, no uncontrolled cloud deployments, and separate accounting for shared GitHub/development tooling. Shared engineering costs do not block M01 solely because they are nonzero.
- [ ] Version and changelog match the tag, and release notes explicitly distinguish foundations from functional MVP.
- [ ] Only synthetic test data is shipped; audit release archives.
- [ ] Release tag is annotated, points to an authorized `main` commit and is pushed with owner approval.
- [ ] Published release links and SHA256 asset hashes are checked.
- [ ] No real-data production deployment; authentication and server-side privacy gates are M02.

Product version stays **0.1.0 until the M02 private-family MVP is accepted**. No milestone/feature/patch bump occurs beforehand. See [versioning](versioning.md).
