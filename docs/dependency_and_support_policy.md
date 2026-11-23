# Dependency, security and .NET support policy

## License
FamilyTree's root LICENSE is Apache License 2.0 (OSI-approved). Do not assume compatible redistribution for third-party genealogy code, media, hosted APIs or data sets. Verify each package's license before introducing it, and keep attributable notices when required.

## Supported frameworks
The codebase targets **.NET 11 GA**, selected in `global.json` and built by the required `build-test` GitHub Actions job. .NET 11 is a standard-term-support release. Recheck Microsoft's support dates before each milestone; plan upgrades before end of support. No SDK preview/nightly dependencies for production.

## Dependency verification
1. Dependabot proposes weekly GitHub Actions and NuGet updates with repository permissions limited to what is required.
2. Review license, changelog, known vulnerabilities, package provenance and restore reproducibility before merging dependency changes.
3. Run native package vulnerability audit at CI restore; evaluate any audit errors and test update plans.
4. **Every third-party GitHub Action must use an immutable, complete 40-character commit SHA**, not a tag. Record the upstream version as a comment and update SHA only after verifying the tag-to-commit mapping using the upstream project.
5. CI uses `permissions: contents: read`; elevated `contents: write` is allowed only for the manually approved tag release job.
6. Avoid installing telemetry exporters, cloud integrations or libraries that transfer private data unless their behavior and costs have been reviewed.

## Private reporting
The private security report entry point and enablement requirements are documented in SECURITY.md. An administrator must verify that GitHub Private Vulnerability Reporting is enabled before publicly hosting real family data.

## Cost accounting
**Hosting/production infrastructure must have $0 incremental charges** unless specifically authorized. GitHub Actions, Code Quality, Copilot and other engineering tools are development expenses shared across the projects using them, not hosting. Existing entitlements may be used; activate new paid licenses or AI-credit overages only after the owner approves the proposed incremental cost. See [Cost policy](cost_policy.md).
