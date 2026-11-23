# OIDC provider hardening workstream (#9, #10, #46)

## Status
An exact, default-deny **configuration guard** is implemented, but this
does **not** authenticate anyone. It cannot validate JWT signatures or
mint memberships. All private family API routes must remain closed.

## Required server implementation
1. Choose explicitly trusted Google and/or Microsoft identity providers and
   registered client/audience IDs, supplied from encrypted runtime secrets.
   Don't commit tenant IDs, client secrets, refresh tokens or keys.
2. Configure a maintained .NET bearer-token/OIDC authentication handler
   with provider metadata from the exact HTTPS issuer, TLS, JWKS signature
   verification, issuer/audience match, expiration/not-before, signature
   algorithm constraints, clock-skew limit and replay defenses as required.
3. For Microsoft, use an explicit tenant GUID; do not enable `common` or
   `organizations` until supported and tested with secure per-tenant
   issuer policy. Google issuer is fixed.
4. Every authenticated principal must still have an active, server-stored
   family membership with explicit person, relationship and field grants.
   A valid token alone grants **zero** genealogy rights.
5. Keep person claims separately verified and admin-approved, with no
   self-approval. Pause/revoke must invalidate server-side grants immediately.
6. Integration tests must cover forged header, unsigned/expired/wrong-aud
   bearer token, public-key rotation, cross-tenant replay, revoked member,
   unknown person ID and field/media denial equivalence.
7. Remote MCP endpoints must additionally validate host/Origin/TLS,
   permissions, rates and publication approvals. No production connector
   until these gates pass.

## Boundary
`OidcConfigurationGuard` checks static provider configuration and may only
compare **already verified** token claims as an additional assertion. Passing
unverified claims or arbitrary headers to the guard is a security bug.
