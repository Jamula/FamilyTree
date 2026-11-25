$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'This verifier is Windows-only.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $PSScriptRoot 'FamilyTree.RecoveryTests.csproj'
$runDirectory = Join-Path $env:LOCALAPPDATA (
    'FamilyTree\recovery-verification-' + [Guid]::NewGuid().ToString('N'))
$keyDirectory = Join-Path $runDirectory 'key'
$keyPath = Join-Path $keyDirectory 'backup.key'
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentSid = $currentIdentity.User.Value
$previousKeyPath = $env:FAMILYTREE_RECOVERY_TEST_KEY_FILE
$key = $null

function Assert-CurrentUserOnlyAcl([string] $Path, [bool] $RequireProtected) {
    $acl = Get-Acl -LiteralPath $Path
    if ($RequireProtected -and -not $acl.AreAccessRulesProtected) {
        throw "ACL inheritance is enabled for $Path."
    }

    $allowedSids = @(
        $acl.Access |
            Where-Object { $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow } |
            ForEach-Object { $_.IdentityReference.Translate(
                [Security.Principal.SecurityIdentifier]).Value } |
            Sort-Object -Unique
    )
    if ($allowedSids.Count -ne 1 -or $allowedSids[0] -ne $currentSid) {
        throw "Expected only the current user to have allow access to $Path."
    }
    $hasFullControl = @(
        $acl.Access |
            Where-Object {
                $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                $_.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier]).Value -eq $currentSid -and
                ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
                    [Security.AccessControl.FileSystemRights]::FullControl
            }
    ).Count -gt 0
    if (-not $hasFullControl) {
        throw "The current user does not have FullControl on $Path."
    }
}

try {
    New-Item -ItemType Directory -Path $keyDirectory -Force | Out-Null
    $directoryAcl = Get-Acl -LiteralPath $keyDirectory
    $directoryAcl.SetAccessRuleProtection($true, $false)
    $directoryAcl.SetAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $currentIdentity.User,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                [Security.AccessControl.InheritanceFlags]::ObjectInherit,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    Set-Acl -LiteralPath $keyDirectory -AclObject $directoryAcl

    $key = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($key)
    }
    finally {
        $random.Dispose()
    }
    [IO.File]::WriteAllBytes($keyPath, $key)
    [Array]::Clear($key, 0, $key.Length)
    $key = $null
    Assert-CurrentUserOnlyAcl -Path $keyDirectory -RequireProtected $true
    Assert-CurrentUserOnlyAcl -Path $keyPath -RequireProtected $false

    $env:FAMILYTREE_RECOVERY_TEST_KEY_FILE = $keyPath
    Push-Location $repositoryRoot
    try {
        $output = & dotnet run --project $project --configuration Release 2>&1
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) {
            throw "Recovery assertions failed with exit code $exitCode."
        }
        if (($output -join "`n") -match 'SKIP symlink assertions') {
            throw 'Windows symlink assertions were skipped; enable Developer Mode or grant the symlink privilege, then rerun.'
        }
        if (($output -join "`n") -notmatch 'PASS SQLite recovery, WAL, authentication, rollback, path safety and restart tests') {
            throw 'Recovery test completion marker was not found.'
        }
        Write-Host 'PASS Windows recovery assertions used the ACL-protected external backup key.'
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($null -ne $key) {
        [Array]::Clear($key, 0, $key.Length)
    }
    if ($null -eq $previousKeyPath) {
        Remove-Item Env:FAMILYTREE_RECOVERY_TEST_KEY_FILE -ErrorAction SilentlyContinue
    }
    else {
        $env:FAMILYTREE_RECOVERY_TEST_KEY_FILE = $previousKeyPath
    }
    if (Test-Path -LiteralPath $runDirectory) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force
    }
}
