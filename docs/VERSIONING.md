# FamilyTree versioning and release policy

The project begins at **0.1.0**. Versions have the form `MAJOR.MINOR.REVISION`.

This is a **milestone-based versioning policy** that uses SemVer-compatible numeric syntax, but not strict Semantic Versioning: shipping a feature in a revision release is a deliberate exception to SemVer's normal minor-for-features rule.

| Event | Example | Rule |
|---|---|---|
| First completed foundation milestone, M0 | `0.1.0` | First release, once it is ready |
| Significant functionality within the same milestone | `0.1.1`, `0.1.2` | Increase REVISION by 1 |
| Important bug fix or security patch | `0.1.3` | Increase REVISION by 1 |
| Complete next milestone, M1 | `0.2.0` | Increase MINOR by 1 and reset REVISION to 0 |
| Complete M2 | `0.3.0` | Increase MINOR and reset REVISION |
| Complete M3 | `0.4.0` | Increase MINOR and reset REVISION |
| Product reaches stable compatibility commitments | `1.0.0` | Decide and document version-1 compatibility policy |

**Do not increment the minor version for each individual PR.** A completed milestone is the minor-version boundary; material, shippable functionality within that milestone uses a revision increment.

## Source of truth

- `VERSION` stores the release version.
- `Directory.Build.props` sets the same version for all .NET projects.
- `CHANGELOG.md` captures completed releases.
- `vMAJOR.MINOR.REVISION` is the Git tag and GitHub Release name.
- GitHub milestones group issues by release. They are manually created in GitHub, and their issue completion is a release gate.

## Release checklist

1. Close the target milestone's required issues or explicitly document approved deferrals.
2. Confirm all required CI/security checks and reviews pass. No real family data, photos or secrets in published archives.
3. Open a PR to bump `VERSION` and `Directory.Build.props` together and update `CHANGELOG.md`. For the first release they start at `0.1.0`.
4. Merge the reviewed PR to `main`.
5. Create an annotated tag `vX.Y.Z` on the approved `main` commit, then push it.
6. The release workflow validates the tag and version, builds/tests, packages the API and website and publishes GitHub Release assets.
7. Deploying to Azure/Cloudflare is a **separate**, optional, controlled workflow with environments and approvals. A GitHub Release does not deploy private family data.

**Status:** These rules prepare releases. No `v0.1.0` tag or production release should be published until the foundation and release checks are verified.

## Releasing from GitHub CLI (maintainer)

After a release PR is merged into `main` and the local checkout is up to date:

```bash
git switch main
git pull --ff-only
git tag -a v0.1.0 -m "FamilyTree v0.1.0"
git push origin v0.1.0
```

The CI workflow will publish the matching GitHub Release after validation. Do not manually create a conflicting GitHub Release at the same tag.
