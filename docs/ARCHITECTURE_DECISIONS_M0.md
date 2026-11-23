# M0 architecture decisions for the private MVP

These decisions provide a zero-cost, portable development baseline. They do **not** permit production family data until authentication, auditing, backups and a privacy review are complete.

## ADR-011: Database, development
Use **SQLite** with a local file for the single-developer, synthetic-data MVP. The domain and API must depend on an `IGenealogyRepository` abstraction, not SQLite SQL. Ensure files live outside web roots and are not committed. Do not conflate local disk file permissions with per-person authorization or at-rest encryption. SQLite is a development decision, not approval for a shared multi-tenant production database.

For multi-user hosting, evaluate **PostgreSQL** and row-level isolation, migrations, backups, encryption and tenant threat modeling in #10/#16. Adopt a hosted database only after the owner approves costs and after testing its hard spend limits.

## ADR-012: Media storage
Define `IMediaStore` with scoped read/write/delete operations. The initial implementation will use non-public local filesystem storage; all reads are mediated by authenticated, authorized API endpoints. Never put private media under `wwwroot`, publicly readable buckets, or guessable unauthenticated URLs. Remote/blob storage requires a separate security/cost review.

## ADR-013: Integration boundaries
Use provider-neutral adapters for OIDC, GEDCOM, media, notifications and AI. A person's genealogy identity is not their OIDC user identity. External identity providers must not grant access without an application-side approved membership. Full genealogy export must remain possible without commercial providers.

## ADR-014: Development and telemetry
Local Aspire orchestration is **optional**, development-only and must not deploy paid cloud resources. Native .NET diagnostics and OpenTelemetry exporters use allowlisted tags and export only on explicit local configuration; all external exporters are off by default. No real genealogy data in local fixtures.

## Milestone validation
The M0 smoke checks prove the public landing page and health endpoints run and the private placeholder APIs still return HTTP 501. They **do not** prove real authentication, storage, database migrations, consent, media or production privacy.
