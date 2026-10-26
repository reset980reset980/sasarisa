# 사사리사 Windows 검증 도구 설치: 파이썬 가상환경 + 한국어 실시간 인식 모델(약 420 MB)
#   PowerShell 에서:  powershell -ExecutionPolicy Bypass -File .\setup.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command py -ErrorAction SilentlyContinue) -and -not (Get-Command python -ErrorAction SilentlyContinue)) {
    Write-Host "파이썬이 없습니다. 먼저 설치하세요:  winget install Python.Python.3.12" -ForegroundColor Red; exit 1
}
$py = if (Get-Command py -ErrorAction SilentlyContinue) { "py" } else { "python" }

if (-not (Test-Path .venv)) { & $py -m venv .venv; if ($LASTEXITCODE -ne 0) { throw 'Python environment creation failed.' } }
& .\.venv\Scripts\python.exe -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw 'pip update failed.' }
& .\.venv\Scripts\python.exe -m pip install -r requirements.txt
if ($LASTEXITCODE -ne 0) { throw 'Speech dependency installation failed.' }

$name = "sherpa-onnx-streaming-zipformer-korean-2024-06-16"
if (-not (Test-Path "models\$name\tokens.txt")) {
    New-Item -ItemType Directory -Force models | Out-Null
    $url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$name.tar.bz2"
    Write-Host "모델 내려받는 중 (약 420 MB)…"
    curl.exe -L --fail -o "models\$name.tar.bz2" $url
    if ($LASTEXITCODE -ne 0) { throw 'Speech model download failed.' }
    tar.exe -xjf "models\$name.tar.bz2" -C models
    if ($LASTEXITCODE -ne 0) { throw 'Speech model extraction failed.' }
    Remove-Item "models\$name.tar.bz2"
}
Write-Host "`n설치 끝. 실행:  .\.venv\Scripts\python.exe wake_poc.py mic" -ForegroundColor Green
