# Local OIDC developer setup (#9)

This branch adds the ASP.NET Core OIDC code+PKCE challenge and validation
middleware, with both Google and Microsoft **disabled by default**. No
identity provider is configured in source and no live account is required
for public demo or CI. A successful login does **not** grant membership,
genealogy access, an approved profile claim or a family grant. All private
`/api/v1` endpoints remain 501 until #9/#10 integration is reviewed.

## Local HTTPS prerequisite
The session cookie is deliberately Secure, HttpOnly, SameSite=Lax, 8-hour
absolute expiry. Use HTTPS for interactive login, not the HTTP 5181 smoke
endpoint. For local testing, trust the standard .NET developer certificate
with `dotnet dev-certs https --trust` and configure a localhost HTTPS URL
for the API. Register the **exact** HTTPS redirect callback URL with the
identity provider, matching the local listener origin and the suffix below.
Do not expose this development server publicly or share auth cookies.

## Required runtime variables (never commit actual values)

| Provider | Runtime setting | Expected |
|---|---|---|
| Google | `FAMILYTREE_OIDC_GOOGLE_CLIENT_ID` | Registered application ID |
| Google | `FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET` | Secret, secure runtime store |
| Google | Callback | `/auth/callback/google` |
| Microsoft | `FAMILYTREE_OIDC_MICROSOFT_TENANT_ID` | Exact tenant GUID, no `common` |
| Microsoft | `FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID` | Registered application ID |
| Microsoft | `FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET` | Secret, secure runtime store |
| Microsoft | Callback | `/auth/callback/microsoft` |

Never use plain credentials, a user's family names or client secrets in
GitHub issues, screenshots or the public repo. Configuring only part of one
provider fails application startup by design.

## Verification and remaining release gates
- `/auth/providers` lists only configured providers (not clients or secrets).
- `/auth/session` gives 401 before authentication and only returns a
  bool showing login state afterwards, never user names or token contents.
- Missing provider login returns 404. OIDC failures return a generic error.
- Full OIDC is **not integration-tested** against live Google/Microsoft
  accounts until owner provides a safe developer application registration.
- Production needs trusted reverse-proxy handling, CSRF/logout,
  session revocation, persistent active memberships, independent claims,
  fine-grained authorization, safe audit, and live-provider integration tests.
  Release the private API only after all checks in #9, #10 and #46 pass.
