# Contributing to FamilyTree

Thanks for helping build a privacy-first, open, standards-based family heritage platform.

## Workflow
1. Find or create a GitHub issue with a user story and testable acceptance criteria.
2. Keep one concern per branch and pull request; refer to the issue number.
3. Add or update tests, documentation, and privacy review notes.
4. Use .NET 10; run `dotnet restore FamilyTree.slnx`, `dotnet build FamilyTree.slnx -c Release`, and `dotnet run --project tests/FamilyTree.Tests/FamilyTree.Tests.csproj -c Release`.
5. Submit a PR; do not bypass required review or checks.

## Data policy
Never upload real genealogy records, living people's dates, photos, email addresses, or confidential details. Use synthetic individuals and generic fixtures. Do not include customer or family data in bug reports or AI prompts.

## Standards and extension points
Prefer GEDCOM 7, OpenAPI, OIDC and maintained libraries. Respect the licenses of Gramps, webtrees and third-party components. Domain logic must not depend on hosted-only billing or proprietary AI providers.

## Licensing status
An open-source license has not yet been chosen. Do not assume a right to copy or redistribute external code into this repository before its license is checked and our project license is selected.
