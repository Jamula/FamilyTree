# Local private API integration (#105)

The private API stays disabled unless `FAMILYTREE_PRIVATE_API_ENABLED=true`.
When enabled, it is accepted only in the ASP.NET Core `Development`
environment on Linux and every private request must arrive from a loopback
address with a `localhost` or loopback host. Use a localhost listener; never
bind this mode to a public interface or proxy it remotely.

Windows opt-in is an explicit, tested blocker: startup rejects the mode until
a secure Windows ACL implementation and Windows-specific acceptance tests are
available. Do not work around the platform check or run this mode with private
data on Windows.

## Local configuration

Use the OIDC setup in [oidc_local_setup.md](oidc_local_setup.md), a dedicated
local provider registration, and runtime-only values. Keep the data directory
private to the current user. The application rejects directories with
group/other access or symbolic-link storage.

```sh
umask 077
export ASPNETCORE_ENVIRONMENT=Development
export FAMILYTREE_PRIVATE_API_ENABLED=true
export FAMILYTREE_PRIVATE_API_DATA_DIRECTORY="$HOME/.local/share/familytree-private"
mkdir -p "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY"
chmod 700 "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY"
export FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY="$(openssl rand -base64 32)"
export FAMILYTREE_PRIVATE_API_IDENTITY_KEY="$(openssl rand -base64 32)"
export FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE="$HOME/.local/share/familytree-private/data-protection.pfx"
export FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD="$(openssl rand -base64 32)"
openssl req -x509 -newkey rsa:3072 -nodes -days 365 \
  -keyout "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY/local.key" \
  -out "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY/local.crt" \
  -subj '/CN=localhost' \
  -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
  -addext 'extendedKeyUsage=serverAuth'
openssl pkcs12 -export \
  -inkey "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY/local.key" \
  -in "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY/local.crt" \
  -out "$FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE" \
  -passout env:FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD
chmod 600 "$FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE"
rm "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY/local.key" \
  "$FAMILYTREE_PRIVATE_API_DATA_DIRECTORY/local.crt"
export FAMILYTREE_OIDC_GOOGLE_CLIENT_ID='local-registration-client-id'
export FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET='local-registration-client-secret'
dotnet run --project src/FamilyTree.Api/FamilyTree.Api.csproj -- \
  --urls https://localhost:5182
```

Create a local RSA certificate with a private key, export it as a password-
protected PKCS#12 (`.pfx`) file inside the data directory, and set its
permissions to `0600`. The configured certificate and password encrypt the
persisted Data Protection cookie keys. Do not copy secret values into
committed configuration, logs, screenshots, or issue comments. The two
256-bit keys and certificate must remain stable for the lifetime of the local
database and must be backed up separately from it. Removing or rotating these
keys invalidates existing cookies or makes encrypted records unreadable.

The composed routes currently support verified identity registration and
transactional invitation acceptance, approval, and revocation for an already
provisioned family. Cookie-authenticated writes require a same-origin request
and antiforgery token from `GET /api/v1/csrf`. `POST /api/v1/me` returns only
the caller's stable opaque principal ID so an inviter can address that
registered identity.

## Deliberately unavailable

Private people/search/tree reads remain `501` until the reviewed #100
transactional membership, family, branch, person, and field-grant adapters
are merged into this branch; #100 remains open and unmerged. Relationship
persistence and its review transactions from #94 are now available, but its
operations enforce family membership rather than the required branch/person/
field grants. Relationship proposal, review, undo, and read routes therefore
remain `501` until the #100 grant contract can authorize them transactionally.
Family creation/bootstrap and administrator-owner provisioning are also not
exposed: the existing `CreateFictionalFamily` operation is explicitly a
fictional local test seam, not an owner authorization mechanism. A verified
OIDC login alone never creates a family or grants membership. GEDCOM routes
remain disabled pending #104; #103's process-level baseline is separate from
this authenticated loopback integration test.

Until secure owner provisioning and the reviewed read/mutation contracts land,
this is not a private-family deployment and must not be used with real family
records. The default configuration and all unsupported private paths continue
to return an empty, `no-store` `501`.
