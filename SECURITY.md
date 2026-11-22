# FamilyTree Security Policy

## Supported releases
FamilyTree is currently a pre-release prototype. No release is approved to handle real family data.

## Reporting vulnerabilities
Do **not** post security vulnerabilities, family records, photographs, credentials or personal data in public issues. Use GitHub private vulnerability reporting if available under this repository's Security tab. If not enabled, ask the maintainers privately for a secure reporting channel before sharing details.

## Security guarantees required before launch
- Enforce deny-by-default access on the API and database query boundary.
- Authenticate using standards-based OIDC and authorize each family, branch, person, field, relationship and media resource.
- Keep genealogy identities separate from user accounts and explicit membership.
- Ensure hidden people are not leaked by counts, searches, graph traversal, notifications, exports, AI tools or media URLs.
- Keep real family fixtures, contact data, exports, passwords and access tokens out of source, CI logs and public issue trackers.
- Perform threat modeling, backup/restore tests, privacy review and data subject-rights testing before real-data deployment.

## Patch and disclosure policy
Until a maintained release exists, vulnerabilities will be triaged case by case. Future supported-version commitments and a security contact will be documented before public hosting.
