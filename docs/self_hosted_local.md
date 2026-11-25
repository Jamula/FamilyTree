# Local-only container smoke (#16)

This is an **engineering foundation**, not a production/private-family deployment.
The API still denies people and tree endpoints (501); only the landing page and
health/capabilities are available. Do not load real genealogy records.

The API and web images are built from this repository's shared codebase; there
is no separate hosted edition. This compose file is for local development only.

```sh
docker compose -f compose.local.yaml build
docker compose -f compose.local.yaml up
# http://127.0.0.1:5182/
# http://127.0.0.1:5181/health
docker compose -f compose.local.yaml down
```

Both services bind to localhost, run as the .NET image's non-root account,
have read-only root filesystems and no mounted private data. No hosted cloud,
database, storage account or paid service is configured.

## Backup and restore status

The compose deployment has no persistent database or media volume, so it
currently has no application data to back up or restore. Do not add a host
volume or place genealogy data in the containers. The encrypted local snapshot
is an isolated development prototype, not wired to the API or Compose and not
a supported backup/restore system; see [its limitations](encrypted_local_snapshot.md).
The [deployment preflight](hosted_deployment_preflight.md#local-backup-and-restore-plan)
records a synthetic-data-only design and the recovery gates that remain.

Do not run publicly, open firewall ports or treat this as a production compose
configuration. Persistence, account authorization, supported backup/restore,
encryption at rest, secret rotation, tenant isolation, data export/erasure,
GDPR review, availability, mobile clients and hosting remain unimplemented
release gates under #9/#10/#16.
