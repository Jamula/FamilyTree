# Local private API integration (#105)

The private API stays disabled unless `FAMILYTREE_PRIVATE_API_ENABLED=true`.
When enabled, it is accepted only in the ASP.NET Core `Development`
environment on Linux or Windows. Every enabled private request must use HTTPS,
arrive from a loopback address, and use a `localhost` or loopback host. Use a
localhost listener; never bind this mode to a public interface or proxy it
remotely.

Linux storage is restricted with owner-only Unix modes. Windows storage and the
Data Protection certificate are restricted to the current Windows user with
protected ACLs; startup verifies the resulting owner and access rules. The
process-level private API assertions run on both platforms. Symlink rejection
is also enforced for Windows reparse points.

## Local configuration

Use the OIDC setup in [oidc_local_setup.md](oidc_local_setup.md), a dedicated
local provider registration, and runtime-only values. Keep the data directory
private to the current user. The application restricts storage to the current
user with OS-specific modes or ACLs and rejects symbolic-link/reparse-point
storage.

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

On Windows, use a dedicated directory below `%LOCALAPPDATA%`, export a
password-protected RSA PKCS#12 certificate into that directory, and provide
the same runtime variables through the process environment. Startup applies
and verifies current-user-only Windows ACLs before using the database or
certificate; do not copy private data into a shared directory.

Example from a PowerShell session (create the certificate and keys once, then
retain the same values in a local secret manager for subsequent runs):

```powershell
$data = Join-Path $env:LOCALAPPDATA 'FamilyTree\private'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($sid)
$rule = [Security.AccessControl.FileSystemAccessRule]::new(
  $sid, [Security.AccessControl.FileSystemRights]::FullControl,
  [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [Security.AccessControl.InheritanceFlags]::ObjectInherit,
  [Security.AccessControl.PropagationFlags]::None,
  [Security.AccessControl.AccessControlType]::Allow)
$acl.AddAccessRule($rule)
[IO.DirectoryInfo]::new($data).Create($acl)
$password = [Convert]::ToBase64String(
  [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$securePassword = ConvertTo-SecureString $password -AsPlainText -Force
$cert = New-SelfSignedCertificate -Type SSLServerAuthentication `
  -DnsName localhost -CertStoreLocation Cert:\CurrentUser\My `
  -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable `
  -NotAfter (Get-Date).AddDays(365)
$certPath = Join-Path $data 'data-protection.pfx'
$export = @{ Cert = $cert; FilePath = $certPath; Password = $securePassword }
Export-PfxCertificate @export
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:FAMILYTREE_PRIVATE_API_ENABLED = 'true'
$env:FAMILYTREE_PRIVATE_API_DATA_DIRECTORY = $data
$env:FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE = $certPath
$env:FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD = $password
$env:FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY =
  [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:FAMILYTREE_PRIVATE_API_IDENTITY_KEY =
  [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project src/FamilyTree.Api/FamilyTree.Api.csproj -- `
  --urls https://localhost:5182
```

The local Aspire AppHost exposes the API on its loopback HTTPS endpoint
(`https://localhost:5183`) and forwards only the allowlisted private API and
OIDC runtime settings when supplied to the AppHost process. Keep secrets in
that process environment or a local secret provider; never put values in
AppHost source or committed settings. Aspire's default configuration still
leaves private mode off, and HTTP remains fail-closed.

Create a local RSA certificate with a private key, export it as a password-
protected PKCS#12 (`.pfx`) file inside the data directory, and set its
permissions to `0600`. The configured certificate and password encrypt the
persisted Data Protection cookie keys. Do not copy secret values into
committed configuration, logs, screenshots, or issue comments. The two
256-bit keys and certificate must remain stable for the lifetime of the local
database and must be backed up separately from it. Removing or rotating these
keys invalidates existing cookies or makes encrypted records unreadable.

The composed routes support verified identity registration and transactional
invitation acceptance, approval, and revocation for an already provisioned
family. Granted person lookup, bounded display-name search, and center-scoped
tree reads use #100's persisted membership/branch/person/field checks inside
the read transaction. Tree results contain only explicitly granted people;
relationship edges remain absent. Cookie-authenticated writes require a
same-origin request and antiforgery token from `GET /api/v1/csrf`.
`POST /api/v1/me` returns only the caller's stable opaque principal ID so an
inviter can address that registered identity.

## Deliberately unavailable

Relationship persistence and review transactions from #94 enforce family
membership but do not enforce branch/person/field grants. Relationship
proposal, review, undo, and read routes therefore remain `501` until those
grants are checked transactionally. Family creation/bootstrap and
administrator-owner provisioning are also not exposed: the existing
`CreateFictionalFamily` operation is a fictional local test seam, not an owner
authorization mechanism. A verified OIDC login alone never creates a family
or grants membership. GEDCOM routes remain disabled pending #104; #103's
process-level baseline remains separately scoped.

Until secure owner provisioning and the reviewed read/mutation contracts land,
this is not a private-family deployment and must not be used with real family
records. The default configuration and all unsupported private paths continue
to return an empty, `no-store` `501`.
