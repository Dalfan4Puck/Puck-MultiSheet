# Shared GitHub push helpers for MultiSheet (local only — not for public repo secrets).

function Get-MultiSheetDeployConfigPath {
    if ($env:MULTISHEET_DEPLOY_CONFIG) {
        return $env:MULTISHEET_DEPLOY_CONFIG
    }
    return Join-Path $env:USERPROFILE "OneDrive\Desktop\Puck Mod\Server & SQL Files\tools\multisheet-deploy.json"
}

function Get-MultiSheetSshKeyPath {
    param([string]$OverrideKey)
    if ($OverrideKey -and (Test-Path $OverrideKey)) { return $OverrideKey }
    if ($env:MULTISHEET_SSH_KEY -and (Test-Path $env:MULTISHEET_SSH_KEY)) {
        return $env:MULTISHEET_SSH_KEY
    }
    $cfgPath = Get-MultiSheetDeployConfigPath
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if ($cfg.SshKey -and (Test-Path $cfg.SshKey)) { return $cfg.SshKey }
    }
    return $null
}

function Test-PublicGitAudit {
    param([string]$Root)
    $patterns = @(
        ('193' + '\.239'),
        ('\b22' + '22\b'),
        ('id' + '_rsa'),
        ('/srv/' + 'puck'),
        ('\bsc' + 'p\b')
    )
    $auditSkip = @('tools/git-push-common.ps1')
    Push-Location $Root
    try {
        $stagedFiles = @(git diff --cached --name-only 2>$null | Where-Object { $_ })
        if ($stagedFiles.Count -eq 0) {
            $stagedFiles = @(git ls-files)
        }
        foreach ($file in $stagedFiles) {
            if (-not $file -or -not (Test-Path $file)) { continue }
            if ($auditSkip -contains ($file -replace '\\', '/')) { continue }
            $content = Get-Content $file -Raw -ErrorAction SilentlyContinue
            if (-not $content) { continue }
            foreach ($pat in $patterns) {
                if ($content -match $pat) {
                    throw "Public git audit failed - $file matches '$pat'"
                }
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Push-MultiSheetGit {
    param(
        [string]$Root,
        [string]$Message,
        [string]$Remote = "origin",
        [string]$Branch = "main",
        [string]$SshKeyPath,
        [switch]$SkipAudit
    )

    if (-not (Test-Path (Join-Path $Root ".git"))) {
        Write-Warning "No git repo in $Root; skipping push."
        return
    }

    $key = Get-MultiSheetSshKeyPath -OverrideKey $SshKeyPath
    if (-not $key) {
        Write-Warning "No SSH key for GitHub push; skipping. Set SshKey in local multisheet-deploy.json."
        return
    }

    Push-Location $Root
    $hasRemote = (git remote) -contains $Remote
    Pop-Location
    if (-not $hasRemote) {
        Write-Warning "No '$Remote' remote configured; skipping commit and push."
        return
    }

    Write-Host "==> Committing and pushing to GitHub ($Remote/$Branch)..." -ForegroundColor Cyan
    Push-Location $Root
    $askpass = $null
    try {
        git add -A
        if (-not $SkipAudit) {
            Test-PublicGitAudit -Root $Root
        }

        $pending = git status --porcelain
        if ($pending) {
            git commit -m $Message
            if ($LASTEXITCODE -ne 0) { throw "git commit failed with exit code $LASTEXITCODE" }
        }
        else {
            Write-Host "Git: working tree clean; pushing existing commits only." -ForegroundColor Yellow
        }

        $keyDir = Split-Path $key -Parent
        $passFile = Join-Path $keyDir "tools\github_ssh_passphrase.txt"
        if (-not (Test-Path $passFile)) { throw "GitHub passphrase file not found: $passFile" }

        $pass = (Get-Content $passFile -First 1).Trim()
        if (-not $pass) { throw "GitHub passphrase file is empty: $passFile" }

        $askpass = Join-Path $env:TEMP "phl-git-askpass-$PID.cmd"
        [System.IO.File]::WriteAllText($askpass, "@echo $pass")
        $env:SSH_ASKPASS = $askpass
        $env:SSH_ASKPASS_REQUIRE = "force"
        $env:DISPLAY = "1"
        $env:GIT_SSH_COMMAND = "ssh -i `"$key`" -o IdentitiesOnly=yes"

        git push -u $Remote $Branch
        if ($LASTEXITCODE -ne 0) { throw "git push failed with exit code $LASTEXITCODE" }
        Write-Host "GitHub push OK" -ForegroundColor Green
    }
    finally {
        Pop-Location
        if ($askpass -and (Test-Path $askpass)) {
            Remove-Item $askpass -Force -ErrorAction SilentlyContinue
        }
        Remove-Item Env:SSH_ASKPASS -ErrorAction SilentlyContinue
        Remove-Item Env:SSH_ASKPASS_REQUIRE -ErrorAction SilentlyContinue
        Remove-Item Env:DISPLAY -ErrorAction SilentlyContinue
        Remove-Item Env:GIT_SSH_COMMAND -ErrorAction SilentlyContinue
    }
}
