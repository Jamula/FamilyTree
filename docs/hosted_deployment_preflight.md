# Deployment, portability and privacy preflight (#16)

**Planning only.** This document records requirements for future self-hosted
backup and hosted deployment; it does not authorize or implement either.
Current containers are loopback-only, empty-state development services.
Private endpoints remain disabled, and only wholly fictional test genealogies
may be used in this public repository.

## Shared-codebase and cost boundary

Self-hosted and any future hosted offering must use the same reviewed
application codebase and data/export formats. A hosted deployment, public
alpha, paid service, cloud resource, production pipeline change, or use of
real family data is out of scope until the M02 dependencies, security/privacy
reviews, and explicit owner approval are complete.

The binding limit is **$0 incremental production hosting and infrastructure
spend**. Do not treat a free tier, billing alert, or presumed credit as a
hard spending cap. Any proposed exception needs prior approval of the named
service and price, worst-case overage, enforceable cap, alternative, and
rollback plan. No provisioning or pricing commitment is made here.

Portability must not be a paid feature or a reason to retain a departing
customer's data. If a hosted plan is later approved, full user-authorized
export and account-deletion requests must remain available without an exit
fee. A freemium proposal must publish the free and paid feature boundaries,
quotas, retention, and support expectations before launch. Quotas must be
measurable and enforced server-side, with a no-overage hard stop rather than
unbounded usage billing. Candidate dimensions to cost and test include
accounts, family storage, media storage, request rates, and backup retention;
no numeric limits or prices are approved yet.

## Local backup and restore plan

There is no supported backup path for the current Compose application because
it has no persistent application data. The encrypted local snapshot prototype
can save and reload a bounded synthetic snapshot, but it is not integrated
with the application and lacks transactional consistency, access-control
policy, key recovery/rotation, migrations, media handling, and tested disaster
recovery. Its round-trip and tamper tests do not establish production
recoverability.

Before calling a self-hosted installation recoverable, a separately reviewed
implementation and synthetic acceptance suite must establish:

1. A versioned, complete export/backup format for authorized genealogy data,
   relationships, metadata, and any supported media, with documented
   integrity checks and explicit handling of data the format cannot preserve.
2. Encryption and file permissions for backup contents; keys stored separately
   from backup files, with documented secure provisioning, recovery, rotation,
   and revocation. A lost key must not be represented as recoverable.
3. Consistent snapshots while writes occur, bounded retention, safe replacement,
   and cleanup of temporary files. Backups must not be copied to an unapproved
   remote service.
4. Restore validation in an isolated empty environment, with schema/version
   migration, integrity failures, wrong/lost keys, interrupted writes,
   duplicate or malformed records, media references, and recovery-point
   documentation exercised using synthetic data.
5. A user-visible, permission-checked export path that is complete and
   portable. Exporting must not bypass field/media permissions or leak other
   tenants' records.

Until those gates pass, retain no real genealogy data in Compose and do not
describe the prototype as a backup solution.

## Hosted isolation, export and deletion threat model

No hosted tenant service or account export/deletion handler is implemented.
The API's reserved private routes remain fail-closed. Before implementation,
threat-model and test at least the following:

| Threat | Required design and verification |
|---|---|
| Cross-tenant ID substitution, enumeration, or guessed identifiers | Bind every request to a server-verified principal and current family/branch grants; scope database reads and writes by tenant. Deny unknown and unauthorized IDs equivalently, including counts, timing where practical, search, errors, caches and logs. |
| Missing or stale membership in a query, export, or background job | Recheck current membership and grants at the operation boundary; revocation must invalidate effective access. Do not trust client-supplied tenant IDs or cached authorization alone. |
| Shared database, cache, search index, object storage, or backup crossing tenants | Enforce tenant scope in every storage key and query; test isolation across each subsystem and backup/restore path. Review administrator access, encryption-key separation, audit and incident procedures before choosing shared infrastructure. |
| Export used to extract another tenant's or hidden field/media data | Require authenticated, explicit user intent and current authorization; apply the same field/media permissions as reads, bound and audit requests without logging exported content, and provide a complete portable format. |
| Deletion removes only the account but leaves access or data copies | Revoke sessions and grants promptly; define what is erased, retained under a documented legal obligation, anonymized, or aged out of backups. Make deletion status and limits understandable; test retries and backup expiry without restoring deleted access. |
| Quota or resource exhaustion causes cross-tenant impact or surprise spend | Apply per-tenant limits and fair resource isolation, return clear limit responses, prevent automatic overages, and test that one tenant cannot exhaust shared capacity. |

These are design and test requirements, not claims that current code passes
hosted isolation, export, deletion, or availability acceptance.

## Privacy and legal release gates

No public hosted release may proceed until a documented privacy review is
approved. With qualified privacy/legal review, determine and document:

- Controller/processor roles and a lawful basis for each purpose, including
  account administration, family data, diagnostics, support, and any optional
  analytics; do not assume one basis covers every purpose.
- Clear notices, consent/objection choices where applicable, age/minor
  safeguards, data minimization, access/correction/portability/erasure
  workflows, and a way to exercise rights without publishing family records.
- Retention periods for active data, account records, audit events, support
  information, and backup copies; deletion propagation and any legal
  exceptions; and tested, documented restoration behavior after deletion.
- A data-processing agreement and subprocessors/security review where
  processing on behalf of customers applies, plus breach response, access
  controls, encryption, key management, and data-location decisions.
- A costed, enforceable $0 hosting plan or explicit owner-approved exception,
  published quotas and product terms, and evidence that export/deletion and
  privacy controls are tested before exposure.

This is a release checklist, not legal advice or a determination of lawful
basis, GDPR compliance, or DPA sufficiency. Until review and all required
implementation/tests pass, keep hosted release and real-data use blocked and
keep issue #16 open.
