# Contributing to FamilyTree

Thanks for helping build a privacy-first, open, standards-based family heritage platform.

## Workflow
1. Find or create a GitHub issue with a user story and testable acceptance criteria.
2. Keep one concern per branch and pull request; refer to the issue number.
3. Add or update tests, documentation, and privacy review notes.
4. Install the SDK selected by `global.json` when present (otherwise use the SDK matching the target frameworks); run `dotnet restore FamilyTree.slnx`, `dotnet build FamilyTree.slnx -c Release`, and `dotnet run --project tests/FamilyTree.Tests/FamilyTree.Tests.csproj -c Release`.
5. Submit a PR; do not bypass required review or checks.

## Data policy
Never upload real genealogy records, living people's dates, photos, email addresses, or confidential details. Use synthetic individuals and generic fixtures. Do not include customer or family data in bug reports or AI prompts.

## Standards and extension points
Prefer GEDCOM 7, OpenAPI, OIDC and maintained libraries. Respect the licenses of Gramps, webtrees and third-party components. Domain logic must not depend on hosted-only billing or proprietary AI providers.

## Licensing status
The repository already contains a root `LICENSE` using **Apache License 2.0**, and current project code is offered under that license. Any proposed license change must be evaluated and approved separately. Third-party code retains its own license obligations: check compatibility before incorporating code from Gramps, webtrees, or other sources.
