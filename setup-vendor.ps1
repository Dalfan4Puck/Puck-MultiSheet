# Refreshes the PuckLargeLevel vendor clone used by PHLPracticeModPack.
# Source: https://github.com/Jake-Porter/PuckLargeLevel

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$vendor = Join-Path $root "_vendor\PuckLargeLevel"

if (Test-Path $vendor) {
    Write-Host "Removing existing vendor clone..."
    Remove-Item -Recurse -Force $vendor
}

Write-Host "Cloning PuckLargeLevel into $vendor"
git clone --depth 1 https://github.com/Jake-Porter/PuckLargeLevel.git $vendor
Write-Host "Done."
