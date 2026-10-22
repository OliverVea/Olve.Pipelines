# Install the `pl` operator CLI on Windows.
#
#   irm https://<instance>/install.ps1 | iex
#
# Downloads the latest pl.exe from an Olve.Pipelines instance (served anonymously at
# /download/pl-win-x64.exe) and drops it on your PATH. Fetched from an instance, the default
# below is that instance (the app rewrites it when serving this script); fetched from the repo,
# it is v1 prod over Tailscale. Override the instance with $env:PL_API_URL and the target dir
# with $env:PL_INSTALL_DIR.
$ErrorActionPreference = 'Stop'

$base = if ($env:PL_API_URL) { $env:PL_API_URL } else { 'https://pipelines-private.ovea.pro' }
$base = $base.TrimEnd('/')
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

# pl's built-in default instance is v1 prod; `pl login --api-url` saves this one in ~\.pl.
Write-Host "Next: pl login --api-url $base"
