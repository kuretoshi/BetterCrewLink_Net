param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') {
    throw 'Version must be a semantic version such as 3.2.8-net-beta.1.'
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$destination = Join-Path $repoRoot (Join-Path 'dist' $Version)
if (Test-Path -LiteralPath $destination) {
    throw "Release output already exists: $destination"
}

$publishDirectory = Join-Path $destination 'publish'
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
$project = Join-Path $repoRoot 'src/TanukiBCL.Client/TanukiBCL.Client.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

foreach ($document in @('README.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $document) -Destination (Join-Path $publishDirectory $document)
}

$required = @(
    'TanukiBCL.Net.exe', 'TanukiBCL.Net.deps.json', 'update-manifest.json',
    'Updater/TanukiBCL.Updater.exe', 'NoSReader/TbclSnapshotReader.exe',
    'RoleReaders/SnrRoleReader.exe', 'README.md', 'LICENSE',
    'Licenses/SourceCodePro-OFL.md'
)
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $item))) {
        throw "Package is missing $item"
    }
}

$archivePath = Join-Path $destination 'TanukiBCL.Net-win-x64.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$zipStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew)
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
Write-Output 'The release was packaged locally; no GitHub Release was created.'
