$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$manifest = Join-Path $PSScriptRoot 'SnrCosmeticsReader/Cargo.toml'
& cargo build --manifest-path $manifest --release --locked
if ($LASTEXITCODE -ne 0) { throw 'SNR cosmetic decoder build failed.' }
$target = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $PSScriptRoot 'SnrCosmeticsReader/target' }
$decoder = Join-Path $target 'release/tbcl-snr-cosmetics.exe'
$output = Join-Path $root 'artifacts/snr-cosmetics'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath $decoder -Destination (Join-Path $output 'tbcl-snr-cosmetics.exe')
