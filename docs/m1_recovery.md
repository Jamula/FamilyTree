# M1 local SQLite recovery utility (#109)

This utility creates an authenticated encrypted snapshot of the local
`FamilyTree.Persistence` SQLite database and restores it into a **new isolated
directory**. It is an engineering recovery lane, not M1 signoff, a production
backup service, or authorization to use real family records. Use synthetic
records only.

The utility uses the supported SQLite online backup API. It does not copy the
live `.sqlite`, `-wal`, or `-shm` files. Committed WAL transactions are
included in the consistent snapshot; uncommitted transactions are not. The
snapshot is serialized in memory and encrypted with AES-256-GCM before being
written. Format metadata and SQLite schema are validated, then the database is
checked with `integrity_check` and `foreign_key_check` before restore is
published.

The backup key is a separate, externally provisioned 32-byte file. It is not
stored in the database, backup, source tree, command line, or logs. Backups
fail closed for invalid keys, failed authentication, incomplete or invalid
SQLite snapshots, symlink/reparse paths, and path collisions (including
case-only and canonically equivalent Unicode path aliases, conservatively on
every OS). New encrypted
backup files are published through a same-directory temporary file and atomic
rename without replacing an existing file. Restore only accepts a nonexistent target with no SQLite sidecars,
validates before writing, stages beside the target, flushes it, then renames
it into place. A failed operation does not replace an existing target. Keep
the restore directory private: the recovered SQLite database is not wholly
encrypted at rest (person names are encrypted by the existing persistence
layer, while relational metadata is not).
The supported family-store table definitions and their constraints must match
the current or supported legacy schema; optional identity, grant, and legacy
relationship table pairs are checked when present. Unexpected triggers, views,
and explicit indexes are rejected. Schema changes require an explicit recovery
compatibility update.
Recovery checks verifier presence and format, but cannot authenticate the
database and identity keys; verify those separately by reopening the restore.
The 1 GiB snapshot limit is checked before copying and serialization, but a
concurrently growing source can still consume memory during the SQLite backup.

## Windows use

Run the reproducible Windows verification from the repository root in
PowerShell:

```powershell
.\tests\FamilyTree.RecoveryTests\verify-windows-recovery.ps1
```

The script provisions a temporary external 32-byte backup key under
`$env:LOCALAPPDATA`, restricts and checks its Windows ACL to the current user,
and runs the synthetic recovery suite using that key for its API and CLI
backup/restore checks. It also requires the suite's symlink tests to run; if
Windows denies symlink creation, enable Developer Mode or grant the required
symlink privilege, then rerun. It deletes the temporary key after the tests.

For regular use outside this disposable verification, use a private,
backed-up key location separate from the encrypted backup destination. Losing
the key prevents recovery; do not put a copy beside the backup.

```powershell
$keyDirectory = Join-Path $env:LOCALAPPDATA 'FamilyTree\recovery'
New-Item -ItemType Directory -Force $keyDirectory | Out-Null
$keyPath = Join-Path $keyDirectory 'backup.key'
if (Test-Path -LiteralPath $keyPath) { throw 'Key already exists; preserve and reuse it.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$acl = Get-Acl -LiteralPath $keyDirectory
$acl.SetAccessRuleProtection($true, $false)
foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleAll($rule) }
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    $identity.User, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $keyDirectory -AclObject $acl
$verified = Get-Acl -LiteralPath $keyDirectory
$allowed = @($verified.Access | Where-Object { $_.AccessControlType -eq 'Allow' })
if (-not $verified.AreAccessRulesProtected -or $allowed.Count -ne 1 -or
    $allowed[0].IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -ne $identity.User.Value -or
    ($allowed[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
        [Security.AccessControl.FileSystemRights]::FullControl) {
    throw 'Key directory ACL is not restricted to the current user.'
}
$key = New-Object byte[] 32
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($key)
    $stream = [IO.File]::Open($keyPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($key, 0, $key.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
finally { $random.Dispose(); [Array]::Clear($key, 0, $key.Length) }
```

The create-new operation fails if a key already exists; preserve and reuse the
existing key rather than overwriting it. The example changes the directory ACL;
use a dedicated directory and review inherited file permissions. Provision and protect this key
independently from the database encryption key and identity-registry HMAC key.
Do not pass key bytes on a command line or commit the key.

```powershell
$database = (Resolve-Path '.\familytree.sqlite').Path
$backupDirectory = Join-Path $env:LOCALAPPDATA 'FamilyTree\backups'
New-Item -ItemType Directory -Force $backupDirectory | Out-Null
$backup = Join-Path $backupDirectory 'familytree-2026-11-24.ftbackup'
dotnet run --project .\tools\recovery\FamilyTree.RecoveryTool.csproj -- `
  backup --database $database --output $backup --key-file $keyPath

$restoreDirectory = Join-Path $env:LOCALAPPDATA 'FamilyTree\recovery-test'
New-Item -ItemType Directory -Force $restoreDirectory | Out-Null
$restored = Join-Path $restoreDirectory 'familytree.sqlite'
dotnet run --project .\tools\recovery\FamilyTree.RecoveryTool.csproj -- `
  restore --backup $backup --database $restored --key-file $keyPath
```

Restore into a private, isolated directory and a new filename; it deliberately
refuses to overwrite a database or any `-wal`, `-shm`, or `-journal` sidecar.
Do not restore over a running application database. The tool rejects
symlink/reparse components but cannot prevent another privileged process from
changing paths between validation and use. Protect source and restore
directories with Windows ACLs and stop processes that could modify the source
during recovery.

## Tests and coverage

```powershell
dotnet run --project .\tests\FamilyTree.RecoveryTests\FamilyTree.RecoveryTests.csproj -c Release
dotnet dotnet-coverage collect "dotnet run --project tests/FamilyTree.RecoveryTests/FamilyTree.RecoveryTests.csproj -c Release" --output recovery-coverage.cobertura.xml --output-format cobertura
dotnet .\tests\FamilyTree.RecoveryTests\bin\Release\net11.0\FamilyTree.RecoveryTests.dll --check-coverage recovery-coverage.cobertura.xml
```

The focused suite uses fictional data and verifies WAL backup, memberships,
approved records, identity registry, grants, revocations and audit state after
a separate-process reopen. It also checks wrong keys, tampering, truncated and
authenticated-corrupt snapshots, existing-target rollback, symlink rejection,
cleanup and non-sensitive failure output. The .NET coverage check enforces 90%
line and branch-coverage floors for the recovery implementation. No shared CI
workflow, version, dependency, or paid service is changed.

The existing `FamilyTree.Tests` executable invokes this recovery suite as a
child process, so the normal CI coverage run includes the recovery implementation
and enforces the repository's 90% Persistence component floor.

## Verification evidence

On 2026-11-25, the focused recovery suite passed on Ubuntu 24.04 x64 with
.NET SDK 11.0.100. It launches the backup and restore CLI commands as separate
processes. Separately, it restores a snapshot and launches a fresh process to
verify restored membership, grants, revocations and audit state. Wrong-key,
authenticated-corruption, truncated-backup, existing-target rollback, and
isolated-target assertions passed. The Linux filesystem allowed the symlink
assertions to run.

The review-fix suite also passed on Ubuntu with legacy schema migration,
malformed schema and unexpected schema object rejection, bounded oversized input, and
deterministic pre-publication collision tests. The recovery component measured
397/408 lines (97.3%) and 132/142 branches (93.0%) with `dotnet-coverage`.
The main assertion suite passed in Debug and Release. The documented PowerShell
blocks were parsed on Linux, not executed on Windows.

GitHub Actions `build-test` for commit `4a2f453` passed on
`ubuntu-latest` (run 45526173084); that is not CI evidence for later changes.
**No Windows execution has been performed
or verified.** The PowerShell commands and Windows ACL guidance above are
instructions only; Windows backup/restore behavior and ACL/key-provisioning
acceptance remain outstanding. Do not treat Linux results as Windows
validation or as completion of #109's Windows acceptance.

No Windows runner is configured in the repository workflows; the build-test
workflow uses `ubuntu-latest`, and no separate authorized Windows runner was
available in this environment. Run the PowerShell verifier on an authorized
Windows machine and record its output before marking Windows backup/restore
and ACL acceptance complete.

This evidence is partial work for #90 and does not close or sign off the M1
private-family MVP. Have the independent security review and coordinate
process-level evidence with #103 before treating recovery as accepted.
