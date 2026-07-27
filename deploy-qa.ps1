# Build MultiSheet and sync to GitHub (Workshop upload is separate).
#
# Steam Workshop item 3771130437: upload dist\MultiSheet.dll after each release build.
# Steam delivers the same item to clients and dedicated servers.
#
# Usage (from this folder):
#   .\deploy-qa.ps1                 # build + commit + push (default)
#   .\deploy-qa.ps1 -SkipGit        # build only
#   .\deploy-qa.ps1 -DeployLocal    # build + push + local Plugins copy (dev)
#   .\deploy-qa.ps1 -Configuration Release

param(
    [switch]$DeployLocal,
    [switch]$SkipGit,
    [string]$Configuration = "Release",
    [string]$GitRemote = "origin",
    [string]$GitBranch = "main",
    [string]$SshKey,
    [string]$LocalPluginDir = "C:\Program Files (x86)\Steam\steamapps\common\Puck\Plugins\DalfMultiSheet"
)

$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$RepoRoot = Split-Path $ProjectRoot -Parent
$Csproj = Join-Path $ProjectRoot "PHLPracticeModPack.csproj"
$BuiltDllName = "MultiSheet.dll"
$LocalDllName = "DalfMultiSheet.dll"
$BuiltDll = Join-Path $ProjectRoot "dist\$BuiltDllName"
$WorkshopItemId = "3771130437"
$GitCommon = Join-Path $ProjectRoot "tools\git-push-common.ps1"

if (-not (Test-Path $GitCommon)) {
    throw "Missing git helper: $GitCommon"
}
. $GitCommon

function Install-Dll([string]$SourceDll, [string]$DestPath) {
    $destDir = Split-Path $DestPath -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    $tempPath = "$DestPath.tmp"
    Copy-Item -Path $SourceDll -Destination $tempPath -Force
    Move-Item -Path $tempPath -Destination $DestPath -Force
}

Write-Host "==> Building $Configuration..." -ForegroundColor Cyan
Push-Location $RepoRoot
try {
    dotnet build $Csproj -c $Configuration --no-incremental
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

if (-not (Test-Path $BuiltDll)) {
    throw "Build succeeded but DLL not found: $BuiltDll"
}

$dll = Get-Item $BuiltDll
$buildStamp = $dll.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
Write-Host ""
Write-Host "Build OK — Workshop upload this file:" -ForegroundColor Green
Write-Host "  $($dll.FullName)" -ForegroundColor Cyan
Write-Host "  ($buildStamp, $($dll.Length) bytes)" -ForegroundColor Cyan
Write-Host "  Workshop item: $WorkshopItemId" -ForegroundColor Cyan
Write-Host "  Steam Workshop delivers the DLL to clients and servers after you upload." -ForegroundColor Yellow

if ($DeployLocal) {
    $localDll = Join-Path $LocalPluginDir $LocalDllName
    Write-Host ""
    Write-Host "==> -DeployLocal: copying to client Plugins (dev-only, bypasses Workshop):" -ForegroundColor Yellow
    Write-Host "  $localDll" -ForegroundColor Yellow
    Install-Dll -SourceDll $BuiltDll -DestPath $localDll
    $legacyLocal = Join-Path $LocalPluginDir $BuiltDllName
    if (Test-Path $legacyLocal) {
        Remove-Item $legacyLocal -Force
    }
    Write-Host "Local Plugins copy OK. Fully quit Puck before enabling the plugin." -ForegroundColor Yellow
}

if (-not $SkipGit) {
    $commitMessage = "Build $buildStamp"
    Push-MultiSheetGit -Root $ProjectRoot -Message $commitMessage -Remote $GitRemote -Branch $GitBranch -SshKeyPath $SshKey
}

Write-Host ""
Write-Host "Done. Upload the DLL above via Workshop when you are ready to ship the build." -ForegroundColor Green
