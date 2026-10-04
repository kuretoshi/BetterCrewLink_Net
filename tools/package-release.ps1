param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$DebugAuthFile,
    [switch]$RebuildPreliminary
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') {
    throw 'Version must be a semantic version such as 3.2.8-net-beta.1.'
}
if ($Version -notmatch '-netdev\.' -and [string]::IsNullOrWhiteSpace($DebugAuthFile)) {
    throw 'Beta and stable packages require -DebugAuthFile. Run tools/create-debug-auth.ps1 locally first.'
}

$resolvedDebugAuthFile = $null
if (-not [string]::IsNullOrWhiteSpace($DebugAuthFile)) {
    $resolvedDebugAuthFile = (Resolve-Path -LiteralPath $DebugAuthFile -ErrorAction Stop).Path
    $authFile = Get-Item -LiteralPath $resolvedDebugAuthFile
    if ($authFile.PSIsContainer -or $authFile.Length -le 0 -or $authFile.Length -gt 4096) {
        throw 'Debug auth configuration must be a small nonempty JSON file.'
    }
    $auth = Get-Content -LiteralPath $resolvedDebugAuthFile -Raw | ConvertFrom-Json
    if ($auth.salt -cnotmatch '^[a-f0-9]{32}$' -or $auth.hash -cnotmatch '^[a-f0-9]{64}$') {
        throw 'Debug auth configuration must contain a 16-byte salt and a 32-byte PBKDF2 hash as lowercase hex.'
    }
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$destination = Join-Path $repoRoot (Join-Path 'dist' $Version)
$archivePath = Join-Path $destination 'TanukiBCL.Net-win-x64.zip'
if (Test-Path -LiteralPath $destination) {
    if (-not $RebuildPreliminary -or $Version -notmatch '-net-beta\.' -or
        -not (Test-Path -LiteralPath (Join-Path $destination 'publish') -PathType Container) -or
        -not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
        @((Get-ChildItem -LiteralPath $destination -Force).Name | Where-Object {
            $_ -notin @('publish', 'TanukiBCL.Net-win-x64.zip')
        }).Count -ne 0) {
        throw "Release output already exists or is not a preliminary beta package: $destination"
    }
} elseif ($RebuildPreliminary) {
    throw "No preliminary beta package to rebuild: $destination"
}

$publishDirectory = Join-Path $destination 'publish'
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
$project = Join-Path $repoRoot 'src/TanukiBCL.Client/TanukiBCL.Client.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

foreach ($document in @('README.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $document) -Destination (Join-Path $publishDirectory $document)
}
if ($resolvedDebugAuthFile) {
    Copy-Item -LiteralPath $resolvedDebugAuthFile -Destination (Join-Path $publishDirectory 'debug-auth.json')
}

$required = @(
    'TanukiBCL.Net.exe', 'TanukiBCL.Net.deps.json', 'update-manifest.json',
    'Updater/TanukiBCL.Updater.exe', 'NoSReader/TbclSnapshotReader.exe',
    'RoleReaders/SnrRoleReader.exe', 'README.md', 'LICENSE',
    'Licenses/SourceCodePro-OFL.md'
)
if ($resolvedDebugAuthFile) { $required += 'debug-auth.json' }
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $item))) {
        throw "Package is missing $item"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$zipMode = if ($RebuildPreliminary) { [System.IO.FileMode]::Create } else { [System.IO.FileMode]::CreateNew }
$zipStream = [System.IO.File]::Open($archivePath, $zipMode)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -Recurse -File) {
            $relative = $file.FullName.Substring($publishDirectory.Length).TrimStart('\', '/')
            $entryName = $relative.Replace('\', '/')
            $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $fileStream = [System.IO.File]::OpenRead($file.FullName)
                try { $fileStream.CopyTo($entryStream) }
                finally { $fileStream.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}
finally { $zipStream.Dispose() }
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) { [void]$names.Add($entry.FullName) }
    foreach ($item in $required) {
        if (-not $names.Contains($item.Replace('\', '/'))) { throw "Archive is missing $item" }
    }
}
finally { $archive.Dispose() }

# The updater also rejects traversal, duplicate or Windows-incompatible entry
# names, so validate the exact archive through its real staging path.
& dotnet (Join-Path $publishDirectory 'TanukiBCL.Net.dll') --update-package-file-test $archivePath
if ($LASTEXITCODE -ne 0) { throw 'Update package staging validation failed.' }

$digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Release ZIP: $archivePath"
Write-Output "Size: $((Get-Item -LiteralPath $archivePath).Length) bytes"
Write-Output "SHA-256: $digest"
if ($Version -match '-net-beta\.') {
    Write-Output 'Preliminary beta ZIP only: run tools/build-installer.ps1 before publishing so the ZIP includes Uninstall.exe.'
} else {
    Write-Output 'The release was packaged locally; no GitHub Release was created.'
}
