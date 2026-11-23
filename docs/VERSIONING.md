# FamilyTree release version policy

The initial product version is **0.1.0**, independently of the .NET runtime version.

The identifier uses `MAJOR.MINOR.REVISION`. This deliberately adapts SemVer's numeric form: **feature-level changes inside a milestone advance REVISION** rather than the conventional SemVer feature MINOR. Document this distinction when creating releases.

| Event | Example | Rule |
|---|---|---|
| M0 Foundation release approved | `0.1.0` | First release only after M0 gates |
| Significant shippable functionality during M0 | `0.1.1`, `0.1.2` | Increment revision |
| Bug or security correction | `0.1.3` | Increment revision |
| M1 Private Family MVP complete | `0.2.0` | Increment minor, reset revision |
| M2 Collaboration and AI complete | `0.3.0` | Increment minor, reset revision |
| M3 Expansion complete | `0.4.0` | Increment minor, reset revision |
| Stable 1.0 | `1.0.0` | Explicit future compatibility decision |

Do not release a new minor for every PR. The version on `main` is the **next planned release version** until an approved tag is created.

## Files and tags
- `VERSION`: release version.
- `Directory.Build.props`: central .NET assembly/package version.
- `CHANGELOG.md`: published changes and planned versions.
- Git tag: `vMAJOR.MINOR.REVISION` (e.g. `v0.1.0`).
- GitHub Releases: release artifacts, checksums and generated notes, using approved public GitHub-hosted runners.

## Release process (human-approved)
1. Milestone owner verifies completed/approved-deferred issues and records all release exceptions.
2. Required `build-test`, HTTP smoke, privacy, security, cost and packaging checks pass.
3. A reviewable PR updates `VERSION`, `Directory.Build.props`, `CHANGELOG.md` together. `0.1.0` is the initial baseline.
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
