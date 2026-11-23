# M0 / 0.1.0 release checklist

This is a pre-release foundation artifact. `0.1.0` cannot be published until every required release gate is complete or a specific exception is approved.

- [ ] All M0 issue acceptance criteria met and evidence linked; unresolved scope is documented and approved.
- [ ] SHA-pinned required CI build/test, domain assertions and executable HTTP smoke checks pass on the release commit.
- [ ] Dependency/security/license review is done; no known exposed secrets or actual genealogy records.
- [ ] Verify SECURITY.md private vulnerability reporting is enabled/tested.
- [ ] No external paid resources or uncontrolled cloud deployments; owner-approved $0 controls remain in effect.
- [ ] Version and changelog match the tag, and release notes explicitly distinguish foundations from functional MVP.
- [ ] Only synthetic test data is shipped; audit release archives.
- [ ] Release tag is annotated, points to an authorized `main` commit and is pushed with owner approval.
- [ ] Published release links and SHA256 asset hashes are checked.
- [ ] No real-data production deployment; authentication and server-side privacy gates are M1.

Release milestones increase the **minor** field; material features/patches within the milestone advance **revision**. See [versioning](VERSIONING.md).
