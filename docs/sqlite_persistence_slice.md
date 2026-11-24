# SQLite persistence integration slice (M1 #9, #12, #90)

This is a **testable persistence component**, not a private-family deployment. It stores family and membership state, single-use invitations, reviewed Person proposals, and content-free audit events across process restarts using transactional SQLite. Person display names are AES-256-GCM encrypted with family/person AAD; the caller supplies a 256-bit key via a private OS secret source. No keys or private data are committed.

The current bootstrap method is **only for fictional local development**. Callers must be authenticated and the identity trusted before any production provisioning. No HTTP handler is wired to this component; `PrivateApiBoundary` remains fully active. Do not put real family data into this store until key management, backup/restore, file ACLs, deployed identity and privacy review are finished.

The local tree read source and person lookup/search adapter compose current active membership with persisted branch/person and independent display-name grants in one SQLite transaction. Revoking person access or membership also removes its display-name grants. Membership alone never grants a read. The source accepts a trusted server principal, not a verified session, and returns no relationships or other fields; it is not an authenticated API or a production authorization boundary.

Next: persist independent relationships and their grants, remaining field and media permissions, and reversible audits; integrate verified OIDC subject binding (#9); and expose only authenticated private API endpoints after security review and full E2E denial tests (#10, #90). Tests use synthetic names and isolated temporary files.
