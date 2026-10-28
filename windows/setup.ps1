$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'poc\setup.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Speech environment setup failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') -m pip check
if ($LASTEXITCODE -ne 0) { throw 'Speech dependency check failed.' }
& (Join-Path $PSScriptRoot 'prepare-vad.ps1') | Out-Null
$model = Join-Path $PSScriptRoot 'poc\models\sherpa-onnx-streaming-zipformer-korean-2024-06-16'
foreach ($file in @('tokens.txt','bpe.model','encoder-epoch-99-avg-1.int8.onnx','decoder-epoch-99-avg-1.int8.onnx','joiner-epoch-99-avg-1.int8.onnx')) {
    if (-not (Test-Path -LiteralPath (Join-Path $model $file))) { throw "Incomplete speech model: $file" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required. Install Microsoft.DotNet.SDK.10 and retry.' }
& dotnet build (Join-Path $PSScriptRoot 'app\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows app build failed.' }
& (Join-Path $PSScriptRoot 'install-shortcut.ps1')
Write-Host 'Ready. Run windows\run.cmd.' -ForegroundColor Green
