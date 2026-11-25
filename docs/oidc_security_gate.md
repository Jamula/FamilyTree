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

## Google-only local development (#126)

The Windows developer launcher is a partial, synthetic-data-only foundation.
It does not configure Microsoft or grant family access. A CI-built package
and Windows/live-provider verification are still outstanding. Until those
gates pass, the following is a source-build procedure, not a release:

```powershell
$devRoot = Join-Path $env:LOCALAPPDATA 'FamilyTreeDev'
dotnet publish src/FamilyTree.Api/FamilyTree.Api.csproj -c Release -o (Join-Path $devRoot 'FamilyTree.Api')
dotnet publish src/FamilyTree.DevGoogle/FamilyTree.DevGoogle.csproj -c Release -o (Join-Path $devRoot 'FamilyTree.DevGoogle')
dotnet dev-certs https --trust
& (Join-Path $devRoot 'FamilyTree.DevGoogle/FamilyTree.DevGoogle.exe') setup
& (Join-Path $devRoot 'FamilyTree.DevGoogle/FamilyTree.DevGoogle.exe') start
```

Install the .NET 11 SDK/runtime for the current Windows user first. `setup`
prompts without echo and stores both Google values in Windows Credential
Manager; never paste them into arguments, source files, screenshots or logs.
Use `status`, `update`, or `remove` in place of `setup` as needed. Do not
record the screen or use a terminal that records keystrokes during intake.
Register the exact callback `https://localhost:5182/auth/callback/google`;
port 5182 must be free. `start` runs only the local API; stop it with Ctrl+C.
The standard HTTPS development certificate is separate from the Microsoft
signing certificate and any database or data-protection keys.

If startup fails, check the installed runtime, trusted development certificate
and port availability without sharing credentials. A successful provider
login alone is not evidence of membership, private API access or revocation.
