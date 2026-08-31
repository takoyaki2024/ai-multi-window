$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'AiMultiWindow.csproj'
$publishDir = Join-Path $repo 'publish\win-x64'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'AI Multi Window.lnk'

Write-Host '[AI Multi Window] Publishing app...'
dotnet publish $project -c Release -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exe = Join-Path $publishDir 'AiMultiWindow.exe'
if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }

$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $publishDir
$shortcut.Description = 'AI Multi Window - 3 Chat'
$shortcut.Save()

Write-Host ''
Write-Host 'Desktop shortcut created:'
Write-Host $shortcutPath
Write-Host 'From now on, double-click AI Multi Window on the desktop.'
