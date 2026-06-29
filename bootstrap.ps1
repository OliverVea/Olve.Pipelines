# Install the `pl` operator CLI on Windows.
#
#   irm https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/main/bootstrap.ps1 | iex
#
# Downloads the latest pl.exe from the Olve.Pipelines instance (served at
# /download/pl-win-x64.exe) and drops it on your PATH. The instance is reachable over
# Tailscale at the default URL below — the same host the CLI talks to by default. Override
# with $env:PL_API_URL (e.g. https://pipelines-beta.ovea.pro) and the target dir with
# $env:PL_INSTALL_DIR.
$ErrorActionPreference = 'Stop'

$base = if ($env:PL_API_URL) { $env:PL_API_URL } else { 'https://pipelines-private.ovea.pro' }
$dest = if ($env:PL_INSTALL_DIR) { $env:PL_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\pl' }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$target = Join-Path $dest 'pl.exe'

Write-Host "Downloading pl-win-x64.exe from $base ..."
Invoke-WebRequest -Uri "$base/download/pl-win-x64.exe" -OutFile $target
Write-Host "Installed pl to $target"

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $dest) {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$dest", 'User')
    Write-Host "Added $dest to your user PATH — restart your shell to pick it up."
}
