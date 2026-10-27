param([string]$Version = '0.2.30')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.build' }
$root = $PSScriptRoot
$cache = Join-Path $root 'artifacts\package-cache'
$buildId = $Version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$stage = Join-Path $root ('artifacts\package-build\' + $buildId)
$output = Join-Path $root 'dist'
$buildPython = Join-Path $root 'poc\.venv\Scripts\python.exe'
New-Item -ItemType Directory -Force $cache,$stage,$output | Out-Null
$archive = Join-Path $cache 'python-3.13.17-embed-amd64.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    & curl.exe -L --fail --silent --show-error --retry 2 -o $archive 'https://www.python.org/ftp/python/3.13.17/python-3.13.17-embed-amd64.zip'
    if ($LASTEXITCODE -ne 0) { throw 'Embedded Python download failed.' }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '9E35319BFFEDAB8B36AD44D8F377CB4F1733BCEC257A0B6D04D94F01486DDB38') { throw 'Embedded Python archive hash mismatch.' }
& dotnet publish (Join-Path $root 'app\VoiceAssistant.Desktop\VoiceAssistant.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Self-contained application publish failed.' }
$python = Join-Path $stage 'python'
Expand-Archive -LiteralPath $archive -DestinationPath $python
$pythonSignature = Get-AuthenticodeSignature -FilePath (Join-Path $python 'python.exe')
if ($pythonSignature.Status -ne 'Valid' -or $pythonSignature.SignerCertificate.Subject -notmatch 'Python Software Foundation') { throw 'Embedded Python publisher verification failed.' }
$wheels = Join-Path $cache 'wheels313'
& $buildPython -m pip download --dest $wheels --only-binary=:all: --platform win_amd64 --python-version 3.13 --implementation cp --abi cp313 --require-hashes -r (Join-Path $root 'runtime\requirements-packaged.txt')
if ($LASTEXITCODE -ne 0) { throw 'Pinned speech wheel download failed.' }
& $buildPython -m pip install --target (Join-Path $python 'Lib\site-packages') --no-compile --no-index --find-links $wheels --only-binary=:all: --platform win_amd64 --python-version 3.13 --implementation cp --abi cp313 --require-hashes -r (Join-Path $root 'runtime\requirements-packaged.txt')
if ($LASTEXITCODE -ne 0) { throw 'Pinned speech package extraction failed.' }
[IO.File]::WriteAllText((Join-Path $python 'python313._pth'), "python313.zip`n.`nLib/site-packages`n../runtime`n../poc`nimport site`n", [Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Force (Join-Path $stage 'runtime'),(Join-Path $stage 'poc') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'runtime\speech_worker.py') -Destination (Join-Path $stage 'runtime')
Copy-Item -LiteralPath (Join-Path $root 'runtime\acoustics.py') -Destination (Join-Path $stage 'runtime')
Copy-Item -LiteralPath (Join-Path $root 'runtime\check_runtime.py') -Destination (Join-Path $stage 'runtime')
Copy-Item -LiteralPath (Join-Path $root 'poc\wake_poc.py') -Destination (Join-Path $stage 'poc')
Copy-Item -LiteralPath (Join-Path $root '..\LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'FEATURE_PARITY.md'),(Join-Path $root 'TOOLS_PARITY.md'),(Join-Path $root 'VERIFICATION.md') -Destination $stage
New-Item -ItemType Directory -Force (Join-Path $stage 'Notices') | Out-Null
foreach ($package in @('microsoft.netcore.app.runtime.win-x64\10.0.5','microsoft.windowsdesktop.app.runtime.win-x64\10.0.5','microsoft.aspnetcore.app.runtime.win-x64\10.0.5','microsoft.web.webview2\1.0.4314.35')) {
    $packageRoot = Join-Path (Join-Path $env:USERPROFILE '.nuget\packages') $package
    $noticeDir = Join-Path (Join-Path $stage 'Notices') ($package.Replace('\','-'))
    New-Item -ItemType Directory -Force $noticeDir | Out-Null
    Get-ChildItem -LiteralPath $packageRoot -File | Where-Object {$_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)'} | Copy-Item -Destination $noticeDir
}
& $buildPython (Join-Path $root 'runtime\package_distribution.py') --stage $stage --windows-root $root --version $Version --wix (Join-Path $root 'installer\obj\Payload.wxs')
if ($LASTEXITCODE -ne 0) { throw 'Distribution manifest/source creation failed.' }
& (Join-Path $python 'python.exe') -B (Join-Path $stage 'runtime\check_runtime.py')
if ($LASTEXITCODE -ne 0) { throw 'Embedded speech runtime could not load.' }
& (Join-Path $python 'python.exe') -B (Join-Path $stage 'runtime\speech_worker.py') --devices
if ($LASTEXITCODE -ne 0) { throw 'Embedded speech device enumeration failed.' }
& dotnet build (Join-Path $root 'installer\Sasarisa.Windows.wixproj') -c Release "-p:PayloadDir=$stage" "-p:BuildVersion=$Version" -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
$msi = Join-Path $output ('Sasarisa-Windows-' + $Version + '-x64.msi')
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($msi + '.sha256'), "$hash  $([IO.Path]::GetFileName($msi))`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output 'last-build.json'), (@{version=$Version;stage=$stage;msi=$msi;sha256=$hash;built=(Get-Date -Format o)} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Installer ready: $msi"
