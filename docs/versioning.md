# FamilyTree release version policy

The initial product version is **0.1.0**, independently of the .NET runtime version.

The owner requires **the product version to remain exactly `0.1.0` until the M02 Private Family MVP is accepted**. Every M01, M02 and early M03 feature, refactor, fix and test commit remains `0.1.0`; no `0.1.1` or `0.2.0` bump is authorized by merging an issue or PR. See the [roadmap](roadmap.md) for the legacy-to-current milestone mapping.

After all M02 core issues #9–#13 pass their acceptance tests, the owner can make a separate, explicit version decision. That decision may choose `0.2.0` or another version; it is **not automatic**.

The `scripts/check-version-freeze.py` CI check enforces VERSION, Directory.Build.props Version, AssemblyVersion and FileVersion. To change the policy after MVP, amend that guard and the changelog in a reviewed PR. The .NET SDK framework version is independent of the product version.

The version on `main` stays `0.1.0` until the owner approves a post-MVP change.

## Files and tags
- `VERSION`: release version.
- `Directory.Build.props`: central .NET assembly/package version.
- `changelog.md`: published changes and planned versions.
- Git tag: `vMAJOR.MINOR.REVISION` (e.g. `v0.1.0`).
- GitHub Releases: release artifacts, checksums and generated notes, using approved public GitHub-hosted runners.

## Release process (human-approved)
1. Milestone owner verifies completed/approved-deferred issues and records all release exceptions.
2. Required `build-test`, HTTP smoke, privacy, security, cost and packaging checks pass.
3. A reviewable PR updates `VERSION`, `Directory.Build.props`, `changelog.md` together. `0.1.0` is the initial baseline.
4. Merge to `main`; **only after explicit release approval**, create and push an annotated `vX.Y.Z` tag on the approved commit.
5. Pushing that tag runs `.github/workflows/release.yml`, which verifies version consistency and ancestor relation to `main` and publishes source-built API/Web bundles and SHA256 checksums as a GitHub Release.
6. Deployment is a **separate step**, never implied by publication. Real family data must not be hosted until authentication and privacy approval are complete.

## Commands to publish after approval
```bash
git switch main
git pull --ff-only
git tag -a v0.1.0 -m "FamilyTree v0.1.0"
git push origin v0.1.0
```

**This PR does not tag, publish or deploy anything.** Repository owner permissions may be needed to create protected tags.
