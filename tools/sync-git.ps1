# Commit and push MultiSheet source without rebuilding.
#
# Usage:
#   .\tools\sync-git.ps1
#   .\tools\sync-git.ps1 -Message "Fix minimap patch"

param(
    [string]$Message,
    [string]$GitRemote = "origin",
    [string]$GitBranch = "main",
    [string]$SshKey,
    [switch]$SkipAudit
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path $PSScriptRoot -Parent
$GitCommon = Join-Path $PSScriptRoot "git-push-common.ps1"
if (-not (Test-Path $GitCommon)) {
    throw "Missing git helper: $GitCommon"
}
. $GitCommon

if (-not $Message) {
    $Message = "Sync " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
}

Push-MultiSheetGit -Root $ProjectRoot -Message $Message -Remote $GitRemote -Branch $GitBranch -SshKeyPath $SshKey -SkipAudit:$SkipAudit
