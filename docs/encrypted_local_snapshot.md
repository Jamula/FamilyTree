# Local encrypted snapshot prototype (#12, #10, #16)

`EncryptedLocalSnapshotStore` writes a version-marked AES-256-GCM snapshot
with a fresh nonce and authenticated metadata. Writes replace an encrypted
file in the same directory, and the unit tests use temporary **fictional**
people. The constructor requires an externally provisioned random 32-byte
key. It does not store, generate durable backups of, log or upload the key.

This is not a production database. It does not implement multi-process
transactions, access-control lists, key rotation/recovery, audit of reads,
version migrations, durable review decisions, safe cloud-hosting tenancy,
per-record encryption, file-locking or filesystem symlink defenses.

**Do not put actual family records or the key in this public repo or
run the private API against this store.** The private API remains disabled.
For production use, select a maintained transactional database and secret
manager, plus explicit owner approval of its hosting/recovery costs.
