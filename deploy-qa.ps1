# Build MultiSheet and assemble dist\ as the full Steam Workshop folder.
#
# Steam Workshop item 3771130437: upload the ENTIRE dist\ folder (not just the DLL).
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
$DistDir = Join-Path $ProjectRoot "dist"
$BuiltDllName = "MultiSheet.dll"
$BuiltDll = Join-Path $DistDir $BuiltDllName
$WorkshopItemId = "3771130437"
$GitCommon = Join-Path $ProjectRoot "tools\git-push-common.ps1"

if (-not (Test-Path $GitCommon)) {
    throw "Missing git helper: $GitCommon"
}
. $GitCommon

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
Write-Host "Build OK — Workshop upload the ENTIRE folder:" -ForegroundColor Green
Write-Host "  $DistDir" -ForegroundColor Cyan
Write-Host "  MultiSheet.dll ($buildStamp, $($dll.Length) bytes)" -ForegroundColor Cyan
Write-Host "  Radio: phlstats /radio/api only (no RadioSongs)" -ForegroundColor Cyan
Write-Host "  Workshop item: $WorkshopItemId" -ForegroundColor Cyan
Write-Host "  See docs\RADIO.md for radio client config." -ForegroundColor Yellow

if ($DeployLocal) {
    Write-Host ""
    Write-Host "==> -DeployLocal: mirroring dist\ → $LocalPluginDir" -ForegroundColor Yellow
    if (-not (Test-Path $LocalPluginDir)) {
        New-Item -ItemType Directory -Path $LocalPluginDir -Force | Out-Null
    }

    # Full package mirror (skip upload guide / rollback leftovers).
    Get-ChildItem $DistDir -Force | ForEach-Object {
        if ($_.Name -in @("UPLOAD.txt", "MultiSheet-ROLLBACK.dll")) { return }
        $dest = Join-Path $LocalPluginDir $_.Name
        if ($_.PSIsContainer) {
            Copy-Item $_.FullName $dest -Recurse -Force
        }
        else {
            Copy-Item $_.FullName $dest -Force
        }
    }

    # Prefer MultiSheet.dll name in Plugins (Workshop name); drop legacy rename if present.
    $legacyRename = Join-Path $LocalPluginDir "DalfMultiSheet.dll"
    if (Test-Path $legacyRename) {
        Remove-Item $legacyRename -Force
    }

    $steamPlugins = "C:\Program Files (x86)\Steam\steamapps\common\Puck\Plugins"
    foreach ($legacy in @("FlamiePrac", "FlamieTraining")) {
        $legacyDir = Join-Path $steamPlugins $legacy
        if (Test-Path $legacyDir) {
            $disabled = "$legacyDir.disabled"
            if (Test-Path $disabled) { Remove-Item $disabled -Recurse -Force }
            Rename-Item -Path $legacyDir -NewName "$legacy.disabled" -Force
            Write-Host "Disabled separate plugin folder: $legacy.disabled" -ForegroundColor Yellow
        }
    }

    Write-Host "Local Plugins mirror OK. Fully quit Puck before enabling." -ForegroundColor Yellow
}

if (-not $SkipGit) {
    $commitMessage = "Build $buildStamp"
    Push-MultiSheetGit -Root $ProjectRoot -Message $commitMessage -Remote $GitRemote -Branch $GitBranch -SshKeyPath $SshKey
}

Write-Host ""
Write-Host "Done. Upload the entire dist\ folder to Workshop $WorkshopItemId." -ForegroundColor Green
Write-Host "On the VPS: remove/disable Plugins/FlamiePrac so Flamie does not load twice." -ForegroundColor Yellow
