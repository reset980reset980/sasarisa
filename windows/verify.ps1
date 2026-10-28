param([switch]$AI, [switch]$Codex, [switch]$YouTube)
$ErrorActionPreference = 'Stop'
& dotnet build (Join-Path $PSScriptRoot 'app\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$exe = Join-Path $PSScriptRoot 'app\VoiceAssistant.Desktop\bin\Release\net10.0-windows\VoiceAssistant.exe'
$checkArgs = @('--self-test','--devices')
if ($AI) { $checkArgs += '--ai' }
if ($Codex) { $checkArgs += '--codex' }
if ($YouTube) { $checkArgs += '--youtube' }
$result = Start-Process $exe -ArgumentList $checkArgs -WindowStyle Hidden -Wait -PassThru
$reportPath = Join-Path $PSScriptRoot 'artifacts\checks.json'
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$report | Select-Object total, failed
$report.checks | Where-Object { -not $_.pass } | Format-List
Write-Host "Detailed report: $reportPath"
if ($result.ExitCode -ne 0) { throw 'A verification check failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') -m py_compile (Join-Path $PSScriptRoot 'runtime\speech_worker.py')
if ($LASTEXITCODE -ne 0) { throw 'Speech worker compilation failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') (Join-Path $PSScriptRoot 'runtime\test_vocabulary.py')
if ($LASTEXITCODE -ne 0) { throw 'Vocabulary integration check failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') -B (Join-Path $PSScriptRoot 'runtime\test_acoustics.py')
if ($LASTEXITCODE -ne 0) { throw 'Acoustic signal checks failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') -B (Join-Path $PSScriptRoot 'runtime\test_worker_control.py')
if ($LASTEXITCODE -ne 0) { throw 'Worker capture and pre-roll checks failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') -B (Join-Path $PSScriptRoot 'runtime\test_worker_backlog.py')
if ($LASTEXITCODE -ne 0) { throw 'Worker backlog and incomplete command checks failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') -B (Join-Path $PSScriptRoot 'runtime\test_names.py')
if ($LASTEXITCODE -ne 0) { throw 'Learned wake-name checks failed.' }
& (Join-Path $PSScriptRoot 'poc\.venv\Scripts\python.exe') -B (Join-Path $PSScriptRoot 'runtime\test_whisper_capture.py')
if ($LASTEXITCODE -ne 0) { throw 'Whisper audio handoff checks failed.' }
