param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$NsisPath
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)-net-beta\.(\d+)$') {
    throw 'Installer version must be a net-beta version, for example 3.2.8-net-beta.1.'
}
$numericVersion = @($Matches[1], $Matches[2], $Matches[3], $Matches[4])
if ($numericVersion | Where-Object { [int]$_ -gt 65535 }) {
    throw 'Numeric installer version components must be at most 65535.'
}
$fileVersion = $numericVersion -join '.'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$releaseDirectory = Join-Path $repoRoot (Join-Path 'dist' $Version)
$publishDirectory = Join-Path $releaseDirectory 'publish'
$icon = Join-Path $PSScriptRoot 'installer-icon.ico'
$template = Join-Path $PSScriptRoot 'installer.nsi'
$uninstallInclude = Join-Path $releaseDirectory 'uninstall-files.nsh'
$outputFile = Join-Path $releaseDirectory ("TanukiBCL.Net-Setup-$Version.exe")

foreach ($required in @(
    (Join-Path $publishDirectory 'TanukiBCL.Net.exe'),
    (Join-Path $publishDirectory 'debug-auth.json'),
    (Join-Path $publishDirectory 'README.md'),
    $icon, $template
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Installer source is missing: $required"
    }
}
if (Test-Path -LiteralPath $outputFile) {
    throw "Installer output already exists: $outputFile"
}

if ([string]::IsNullOrWhiteSpace($NsisPath)) {
    $found = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($found) { $NsisPath = $found.Source }
    if (-not $NsisPath) {
        $cache = Join-Path $env:LOCALAPPDATA 'electron-builder\Cache\nsis-3.0.4.1'
        if (Test-Path -LiteralPath $cache) {
            $found = Get-ChildItem -LiteralPath $cache -Recurse -Filter makensis.exe -File |
                Where-Object { $_.DirectoryName -notlike '*\Bin' } | Select-Object -First 1
            if ($found) { $NsisPath = $found.FullName }
        }
    }
}
if (-not $NsisPath -or -not (Test-Path -LiteralPath $NsisPath -PathType Leaf)) {
    throw 'NSIS makensis.exe was not found. Install NSIS 3.04+ or pass -NsisPath.'
}

$files = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Sort-Object FullName)
$directories = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -Directory |
    Sort-Object @{ Expression = { $_.FullName.Length }; Descending = $true }, FullName)
if ($files.Count -eq 0) { throw 'Publish tree is empty.' }
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($publishDirectory.Length).TrimStart('\', '/')
    if ($relative -match '["$\r\n]') { throw "Unsupported installer file name: $relative" }
    $lines.Add('  Delete "$INSTDIR\' + $relative + '"')
}
foreach ($directory in $directories) {
    $relative = $directory.FullName.Substring($publishDirectory.Length).TrimStart('\', '/')
    if ($relative -match '["$\r\n]') { throw "Unsupported installer directory name: $relative" }
    $lines.Add('  RMDir "$INSTDIR\' + $relative + '"')
}
[IO.File]::WriteAllLines($uninstallInclude, $lines, [Text.UTF8Encoding]::new($false))
$estimatedKilobytes = [int][Math]::Ceiling((($files | Measure-Object Length -Sum).Sum) / 1024d)

& $NsisPath /WX /V2 "/DVERSION=$Version" "/DFILE_VERSION=$fileVersion" "/DPUBLISH_DIR=$publishDirectory" `
    "/DOUTPUT_FILE=$outputFile" "/DICON_FILE=$icon" "/DUNINSTALL_INCLUDE=$uninstallInclude" `
    "/DINSTALL_SIZE_KB=$estimatedKilobytes" $template
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputFile -PathType Leaf)) {
    throw 'NSIS installer compilation failed.'
}
$installer = Get-Item -LiteralPath $outputFile
if ($installer.Length -lt 1000000) { throw 'Installer output is unexpectedly small.' }
$digest = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Installer: $outputFile"
Write-Output "Files: $($files.Count)"
Write-Output "Size: $($installer.Length) bytes"
Write-Output "SHA-256: $digest"
Write-Output 'The installer was built locally; it has not been published.'
