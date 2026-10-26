$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'app\VoiceAssistant.Desktop\bin\Release\net10.0-windows\VoiceAssistant.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Run windows\setup.ps1 first.' }
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutName = -join ([char[]](0xC790,0xBE44,0xC2A4))
$shell = New-Object -ComObject WScript.Shell
$linkPath = Join-Path $desktop ($shortcutName + ' Windows.lnk')
if (Test-Path -LiteralPath $linkPath) { Copy-Item -LiteralPath $linkPath -Destination ($linkPath + '.backup') -Force }
$link = $shell.CreateShortcut($linkPath)
$link.TargetPath = $exe
$link.WorkingDirectory = $PSScriptRoot
$link.Description = 'Windows voice assistant based on Sasarisa'
$link.IconLocation = "$exe,0"
$link.Save()
Write-Output $linkPath
