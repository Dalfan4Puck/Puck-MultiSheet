# Fill dist\ so the whole folder can be uploaded to Steam Workshop.

param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$dist = Join-Path $ProjectRoot "dist"
$flamie = Join-Path $ProjectRoot "_vendor\phltrainingcode-main"

New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dist "config") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dist "assets") -Force | Out-Null

$dllSrc = Join-Path $ProjectRoot "bin\$Configuration\netstandard2.1\MultiSheet.dll"
if (Test-Path $dllSrc) {
    Copy-Item $dllSrc (Join-Path $dist "MultiSheet.dll") -Force
}

$bundle = Join-Path $flamie "trainingprefabs"
if (Test-Path $bundle) {
    Copy-Item $bundle (Join-Path $dist "trainingprefabs") -Force
}

foreach ($name in @("training_layout.example.json", "training_prefab_names.json")) {
    $src = Join-Path $flamie $name
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $dist $name) -Force
    }
}

$layoutExample = Join-Path $flamie "training_layout.example.json"
$layoutDest = Join-Path $dist "training_layout.json"
if ((Test-Path $layoutExample) -and -not (Test-Path $layoutDest)) {
    Copy-Item $layoutExample $layoutDest -Force
}

foreach ($name in @(
    "multi_rink.example.json",
    "multisheet_client.example.json",
    "radio_client.example.json",
    "radio_playlist.json",
    "radio_playlist.example.json"
)) {
    $src = Join-Path $ProjectRoot "config\$name"
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $dist "config\$name") -Force
    }
}

$rinkExample = Join-Path $ProjectRoot "config\multi_rink.example.json"
$rinkDest = Join-Path $dist "config\multi_rink.json"
if ((Test-Path $rinkExample) -and -not (Test-Path $rinkDest)) {
    Copy-Item $rinkExample $rinkDest -Force
}

$assetsReadme = Join-Path $ProjectRoot "assets\README.md"
if (Test-Path $assetsReadme) {
    Copy-Item $assetsReadme (Join-Path $dist "assets\README.md") -Force
}

# Never ship RadioSongs — radio streams from phlstats only.
$staleRadio = Join-Path $dist "RadioSongs"
if (Test-Path $staleRadio) {
    Remove-Item $staleRadio -Recurse -Force
}

$rollback = Join-Path $dist "MultiSheet-ROLLBACK.dll"
if (Test-Path $rollback) {
    Remove-Item $rollback -Force
}

Write-Host ("Workshop dist ready: {0}" -f $dist)
Write-Host "  Radio: phlstats /radio/api only (no RadioSongs in package)."
