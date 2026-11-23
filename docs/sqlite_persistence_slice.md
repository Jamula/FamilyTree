# SQLite persistence integration slice (M1 #9, #12, #90)

This is a **testable persistence component**, not a private-family deployment. It stores family and membership state, single-use invitations, reviewed Person proposals, and content-free audit events across process restarts using transactional SQLite. Person display names are AES-256-GCM encrypted with family/person AAD; the caller supplies a 256-bit key via a private OS secret source. No keys or private data are committed.

The current bootstrap method is **only for fictional local development**. Callers must be authenticated and the identity trusted before any production provisioning. No HTTP handler is wired to this component; `PrivateApiBoundary` remains fully active. Do not put real family data into this store until key management, backup/restore, file ACLs, deployed identity and privacy review are finished.

Next: persist independent relationships and reversible audits, integrate verified OIDC subject binding (#9), enforce person/field/branch scopes (#10), and expose only authenticated private API endpoints after full E2E denial tests (#90). Tests use synthetic names and isolated temporary files.
