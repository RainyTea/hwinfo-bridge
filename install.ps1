# hwinfo-bridge installer
#
# PowerShell:
#   irm https://raw.githubusercontent.com/RainyTea/hwinfo-bridge/main/install.ps1 | iex
#
# cmd.exe / Run dialog:
#   powershell -ExecutionPolicy Bypass -NoProfile -Command "irm https://raw.githubusercontent.com/RainyTea/hwinfo-bridge/main/install.ps1 | iex"
#
# What it does:
#   1. Downloads the latest hwinfo-bridge release zip from GitHub
#   2. Stops any running hwinfo-bridge.exe
#   3. Extracts to %LOCALAPPDATA%\HwInfoBridge   (preserves an existing config.json)
#   4. Registers autostart at login (via hwinfo-bridge.exe --install)
#   5. Launches it
#
# Flags:
#   -NoAutostart   skip step 4
#   -NoLaunch      skip step 5
#   -Version v0.2  install a specific tag instead of the latest release

[CmdletBinding()]
param(
    [string] $Version,
    [switch] $NoAutostart,
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
# Force TLS 1.2 — needed when invoked under Windows PowerShell 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = 'RainyTea/hwinfo-bridge'

$InstallDir = Join-Path $env:LOCALAPPDATA 'HwInfoBridge'
$ExePath    = Join-Path $InstallDir 'hwinfo-bridge.exe'
$ConfigPath = Join-Path $InstallDir 'config.json'
$TempZip    = Join-Path $env:TEMP   "hwinfo-bridge-$([guid]::NewGuid()).zip"
$TempDir    = Join-Path $env:TEMP   "hwinfo-bridge-$([guid]::NewGuid())"

# Resolve release + asset URL via the GitHub API
$apiUrl = if ($Version) {
    "https://api.github.com/repos/$Repo/releases/tags/$Version"
} else {
    "https://api.github.com/repos/$Repo/releases/latest"
}

Write-Host "Querying $apiUrl"
$release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'hwinfo-bridge-installer' }
$asset = $release.assets | Where-Object { $_.name -like 'hwinfo-bridge-*.zip' } | Select-Object -First 1
if (-not $asset) { throw "No hwinfo-bridge-*.zip asset found on release $($release.tag_name)" }

Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $TempZip -UseBasicParsing

# Stop a running copy so we can overwrite the exe
Get-Process -Name 'hwinfo-bridge' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running hwinfo-bridge.exe (pid $($_.Id))"
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

# Preserve existing user config if any
$savedConfig = $null
if (Test-Path $ConfigPath) {
    $savedConfig = Get-Content $ConfigPath -Raw
}

New-Item -ItemType Directory -Force -Path $InstallDir, $TempDir | Out-Null
Expand-Archive -Path $TempZip -DestinationPath $TempDir -Force

# Find the published payload inside the archive (it may be at the root or under a subdir)
$payloadRoot = (Get-ChildItem -Path $TempDir -Recurse -Filter 'hwinfo-bridge.exe' | Select-Object -First 1).Directory
if (-not $payloadRoot) { throw 'hwinfo-bridge.exe was not present in the downloaded archive.' }

Copy-Item -Path (Join-Path $payloadRoot.FullName '*') -Destination $InstallDir -Recurse -Force

if ($savedConfig) {
    Set-Content -Path $ConfigPath -Value $savedConfig -NoNewline
    Write-Host "Preserved existing config.json"
}

Remove-Item $TempZip -Force -ErrorAction SilentlyContinue
Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue

if (-not $NoAutostart) {
    Write-Host "Registering autostart"
    & $ExePath --install | Out-Null
}

if (-not $NoLaunch) {
    Write-Host "Launching $ExePath"
    Start-Process -FilePath $ExePath -WindowStyle Hidden
}

Write-Host ""
Write-Host "Installed to $InstallDir"
Write-Host "Verify with: curl http://127.0.0.1:8765/sensors"
