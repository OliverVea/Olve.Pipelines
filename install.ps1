# Install the `pl` operator CLI on Windows.
#
#   irm https://<instance>/install.ps1 | iex                                                     # the instance's pl
#   irm https://raw.githubusercontent.com/OliverVea/Olve.Pipelines/v2/install.ps1 | iex           # the latest release
#
# Served by an instance, it installs the pl that instance was deployed with (/download/pl-win-x64.exe);
# from the repo, the latest GitHub release. $env:PL_API_URL picks an instance, $env:PL_INSTALL_DIR the
# target dir.
$ErrorActionPreference = 'Stop'

$instance = '__OLVE_INSTANCE__'
$releases = 'https://github.com/OliverVea/Olve.Pipelines/releases/latest/download'
$dest = if ($env:PL_INSTALL_DIR) { $env:PL_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\pl' }

if ($env:PL_API_URL) { $base = $env:PL_API_URL.TrimEnd('/'); $url = "$base/download/pl-win-x64.exe" }
elseif ($instance.StartsWith('__OLVE')) { $base = ''; $url = "$releases/pl-win-x64.exe" }
else { $base = $instance.TrimEnd('/'); $url = "$base/download/pl-win-x64.exe" }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$target = Join-Path $dest 'pl.exe'

Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $target
Write-Host "Installed pl to $target"

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $dest) {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$dest", 'User')
    Write-Host "Added $dest to your user PATH — restart your shell to pick it up."
}

$login = if ($base) { $base } else { 'https://<instance>' }
Write-Host "Next: pl login --api-url $login"
