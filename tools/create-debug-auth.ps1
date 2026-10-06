param(
    [string]$OutputPath = (Join-Path $env:APPDATA 'TanukiBCL.Net\release-debug-auth.json'),
    # 3.2.9: keep existing passwords and append one for a named tester (up to 16).
    [switch]$Add,
    [string]$Name
)

$ErrorActionPreference = 'Stop'
$records = @()
if ($Add) {
    if (Test-Path -LiteralPath $OutputPath) {
        $existing = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
        if ($existing.PSObject.Properties.Name -contains 'url') {
            throw 'This file selects HTTPS authentication. Register testers with invitation codes instead.'
        }
        if ($existing.PSObject.Properties.Name -contains 'passwords') {
            if ($existing.passwords -isnot [array]) { throw 'Invalid password configuration.' }
            $records = @($existing.passwords)
        } else {
            $records = @([pscustomobject]@{ name = 'developer'; salt = $existing.salt; hash = $existing.hash })
        }
        foreach ($entry in $records) {
            if ($entry.salt -isnot [string] -or $entry.hash -isnot [string] -or
                $entry.salt -cnotmatch '^[a-f0-9]{32}$' -or $entry.hash -cnotmatch '^[a-f0-9]{64}$') {
                throw 'Invalid password configuration. Existing passwords were not changed.'
            }
        }
    }
    if ($records.Count -ge 16) { throw 'At most 16 debug passwords can be configured.' }
    if ([string]::IsNullOrWhiteSpace($Name)) { $Name = Read-Host 'Name for this tester (not the password)' }
    $Name = $Name.Trim()
    if (-not $Name -or $Name.Length -gt 80) { throw 'Name must contain 1-80 characters.' }
    if ($records | Where-Object { $_.name -eq $Name }) { throw 'This name already exists. Choose another name.' }
} elseif (Test-Path -LiteralPath $OutputPath) {
    throw "The auth file already exists: $OutputPath. Use -Add to keep it, or choose another -OutputPath to rotate the password."
}

$firstSecure = Read-Host 'DEBUG PASSWORD (MINIMUM 16 CHARACTERS)' -AsSecureString
$secondSecure = Read-Host 'Enter the same password again to confirm' -AsSecureString

function Read-SecretText([System.Security.SecureString]$secret) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

$first = Read-SecretText $firstSecure
$second = Read-SecretText $secondSecure
try {
    if ($first.Length -lt 16 -or $first.Length -gt 1024) {
        throw 'Password too short or too long. Use 16-1024 characters and run this script again. No password was saved.'
    }
    if (-not [string]::Equals($first, $second, [StringComparison]::Ordinal)) {
        throw 'The two passwords did not match. Run this script again. No password was saved.'
    }

    $salt = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($salt) }
    finally { $rng.Dispose() }
    $derive = [System.Security.Cryptography.Rfc2898DeriveBytes]::new(
        $first, $salt, 100000, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $hash = $derive.GetBytes(32) }
    finally { $derive.Dispose() }

    $record = [ordered]@{
        salt = [BitConverter]::ToString($salt).Replace('-', '').ToLowerInvariant()
        hash = [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
    }
    if ($Add) {
        $record.name = $Name
        $configuration = @{ passwords = @($records) + @([pscustomobject]$record) } | ConvertTo-Json -Depth 4 -Compress
    } else {
        $configuration = $record | ConvertTo-Json -Compress
    }
    $parent = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($OutputPath, $configuration, [Text.UTF8Encoding]::new($false))
    Write-Output "Wrote salted PBKDF2-SHA256 debug authentication to $OutputPath"
    if ($Add) { Write-Output 'Debug password added. Existing passwords still work.' }
    Write-Output 'The plaintext password was not written to disk or printed.'
    Write-Output 'Package with -DebugAuthUrl "" -DebugAuthFile to embed these local hashes.'
}
finally {
    $first = $null
    $second = $null
    $firstSecure.Dispose()
    $secondSecure.Dispose()
}
