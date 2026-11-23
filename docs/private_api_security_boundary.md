# Pre-MVP Family API security boundary (#9, #10, #46)

## Shipping state: closed by design
The public API intentionally exposes only `/health` and
`/api/v1/capabilities`. The family-data HTTP namespaces are reserved by
`PrivateApiBoundary`, which returns a uniform **501 and an empty body**
regardless of method, person ID, forged `Authorization`, `X-Family-ID`
or `X-Forwarded-User` header. Sensitive data must not be provided to the
public web demo, which only uses the 32-person fictional catalog.

The release gate applies to people, tree, family, membership, media,
search, admin, invitations, person claims, relationships, import/export,
stories, gratitude, AI, and agent/MCP endpoints. It does **not**
authenticate requests or replace server-side authorization.

## Gates required before enabling any private endpoint
1. Configure and prove real Google/Microsoft OIDC or JWT authentication
   using pinned trusted issuer/audience and verified signatures. Raw
   user headers, forwarded headers without a trusted proxy boundary, or
   unsigned JWTs never establish a principal.
2. Resolve the verified principal to active, persisted, non-revoked
   membership. A valid token without a grant authorizes nothing.
3. Apply family, branch, person, relationship, field and media scopes
   before querying, graph traversing, exporting, retrieving for AI or
   using an MCP tool. Unknown and forbidden record IDs must produce
   indistinguishable responses.
4. Require independent reviewer approval of profile claims and edits;
   no self approval, silent AI write or agent publication.
5. Add integration tests for expired/replayed/mismatched tokens, forged
   user headers, cross-family enumeration, unauthorized media, minors,
   tenant revocation and denied export. Add privacy-safe audit events.
6. Only after tests and security reviews pass, replace the pre-MVP
   boundary with explicit authenticated route groups; never remove it
   before protected handlers exist.

## Operational controls
No external provider, secret, production hosting or real family record
is required by this PR. `0.1.0` product version remains frozen.
The CI HTTP smoke tests are a release canary for the fail-closed policy.
