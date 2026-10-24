$ErrorActionPreference = 'Stop'
$speechRoot = Split-Path $PSScriptRoot -Parent
$speechModel = Join-Path $speechRoot 'resources/speech/model'
if (Test-Path -LiteralPath (Join-Path $speechModel 'am/final.mdl')) { return }
$speechCache = Join-Path $speechRoot '.cache/speech'
New-Item -ItemType Directory -Force -Path $speechCache | Out-Null
$speechArchive = Join-Path $speechCache 'vosk-model-small-ja-0.22.zip'
if (!(Test-Path -LiteralPath $speechArchive)) {
    Invoke-WebRequest -Uri 'https://alphacephei.com/vosk/models/vosk-model-small-ja-0.22.zip' -OutFile $speechArchive
}
if ((Get-FileHash -LiteralPath $speechArchive -Algorithm SHA256).Hash -ne 'EFA092D280153A77615E9E0C7D7283E93E600DE3D19D3BEC686C57EF19D52EAC') {
    throw 'Japanese speech model checksum mismatch.'
}
Expand-Archive -LiteralPath $speechArchive -DestinationPath (Join-Path $speechCache 'extracted') -Force
New-Item -ItemType Directory -Force -Path $speechModel | Out-Null
Copy-Item -Path (Join-Path $speechCache 'extracted/vosk-model-small-ja-0.22/*') -Destination $speechModel -Recurse -Force
