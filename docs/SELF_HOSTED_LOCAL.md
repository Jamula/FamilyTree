# Local-only container smoke (#16)

This is an **engineering foundation**, not a production/private-family deployment.
The API still denies people and tree endpoints (501); only the landing page and
health/capabilities are available. Do not load real genealogy records.

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

**Not yet implemented:** persistence, account authorization, database and
media backup/restore, encryption at rest, secret rotation, tenant isolation,
data export/erasure, GDPR review, availability, mobile clients or hosting.
These are release gates under #9/#10/#16. Do not run publicly, open firewall
ports or treat this as a production compose configuration.

After the private MVP is authorized, plan versioned GEDCOM+sidecar backup and
restoration with integrity checks, field/media privacy controls and test
recovery under a separate approved issue. The $0 incremental hosting policy
remains binding.
