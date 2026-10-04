param(
    [string]$OutputPath = (Join-Path $env:APPDATA 'TanukiBCL.Net\release-debug-auth.json')
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) {
    throw "The auth file already exists: $OutputPath. Choose another -OutputPath to rotate the password."
}

$firstSecure = Read-Host 'COMMON DEBUG PASSWORD (MINIMUM 16 CHARACTERS)' -AsSecureString
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

    $configuration = @{
        salt = [BitConverter]::ToString($salt).Replace('-', '').ToLowerInvariant()
        hash = [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
    } | ConvertTo-Json -Compress
    $parent = Split-Path -Parent $OutputPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($OutputPath, $configuration, [Text.UTF8Encoding]::new($false))
    Write-Output "Wrote salted PBKDF2-SHA256 debug authentication to $OutputPath"
    Write-Output 'The plaintext password was not written to disk or printed.'
}
finally {
    $first = $null
    $second = $null
    $firstSecure.Dispose()
    $secondSecure.Dispose()
}
