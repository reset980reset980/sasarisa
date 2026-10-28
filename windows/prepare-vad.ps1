$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot 'artifacts\package-cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$target = Join-Path $cache 'silero_vad.onnx'
$sha256 = '9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6'
if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -ne 643854 -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sha256) {
    $partial = $target + '.download'
    & curl.exe -L --fail --silent --show-error --retry 2 -o $partial 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx'
    if ($LASTEXITCODE -ne 0) { throw 'Silero speech detector download failed.' }
    if ((Get-Item -LiteralPath $partial).Length -ne 643854 -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $sha256) { throw 'Silero speech detector hash mismatch.' }
    Move-Item -LiteralPath $partial -Destination $target -Force
}
$target
