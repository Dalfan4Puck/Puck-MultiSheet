# Shared GitHub push helpers for Dalfan4Puck repos (local only — not for public repo secrets).
#
# Default: HTTPS + Windows Git Credential Manager (no SSH passphrase / SSH_ASKPASS).
# Opt-in legacy SSH: set "GitPushMethod": "ssh" in tools/multisheet-deploy.json

function Get-MultiSheetDeployConfigPath {
    if ($env:MULTISHEET_DEPLOY_CONFIG) {
        return $env:MULTISHEET_DEPLOY_CONFIG
    }
    return Join-Path $env:USERPROFILE "OneDrive\Desktop\Puck Mod\Server & SQL Files\tools\multisheet-deploy.json"
}

function Get-DalfDeployConfig {
    $cfgPath = Get-MultiSheetDeployConfigPath
    $method = "https"
    $sshKey = $null
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if ($cfg.GitPushMethod) {
            $method = "$($cfg.GitPushMethod)".Trim().ToLowerInvariant()
        }
        if ($cfg.SshKey -and (Test-Path $cfg.SshKey)) {
            $sshKey = $cfg.SshKey
        }
    }
    if ($env:MULTISHEET_SSH_KEY -and (Test-Path $env:MULTISHEET_SSH_KEY)) {
        $sshKey = $env:MULTISHEET_SSH_KEY
    }
    if ($env:DALF_GIT_PUSH_METHOD) {
        $method = "$($env:DALF_GIT_PUSH_METHOD)".Trim().ToLowerInvariant()
    }
    return [pscustomobject]@{
        GitPushMethod = $method
        SshKey        = $sshKey
    }
}

function Get-MultiSheetSshKeyPath {
    param([string]$OverrideKey)
    if ($OverrideKey -and (Test-Path $OverrideKey)) { return $OverrideKey }
    return (Get-DalfDeployConfig).SshKey
}

function ConvertTo-GitHubHttpsUrl {
    param([string]$RemoteUrl)
    if ([string]::IsNullOrWhiteSpace($RemoteUrl)) { return $RemoteUrl }
    $url = $RemoteUrl.Trim()
    if ($url -match '^https://github\.com/') {
        if ($url -notmatch '\.git$') { return "$url.git" }
        return $url
    }
    if ($url -match '^git@github\.com:(.+?)(?:\.git)?$') {
        return "https://github.com/$($Matches[1]).git"
    }
    if ($url -match '^ssh://git@github\.com/(.+?)(?:\.git)?$') {
        return "https://github.com/$($Matches[1]).git"
    }
    return $url
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

function Invoke-DalfGitPush {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Remote = "origin",
        [string]$Branch = "main",
        [string]$PushMethod,
        [string]$SshKeyPath
    )

    if (-not (Test-Path (Join-Path $Root ".git"))) {
        throw "No git repo in $Root"
    }

    Push-Location $Root
    try {
        $remotes = @(git remote 2>$null)
        if ($remotes -notcontains $Remote) {
            throw "Remote '$Remote' not configured in $Root"
        }

        $cfg = Get-DalfDeployConfig
        $method = if ($PushMethod) { "$PushMethod".Trim().ToLowerInvariant() } else { $cfg.GitPushMethod }
        if ($method -ne "ssh") { $method = "https" }

        Remove-Item Env:GIT_SSH_COMMAND -ErrorAction SilentlyContinue
        Remove-Item Env:SSH_ASKPASS -ErrorAction SilentlyContinue
        Remove-Item Env:SSH_ASKPASS_REQUIRE -ErrorAction SilentlyContinue
        Remove-Item Env:DISPLAY -ErrorAction SilentlyContinue

        if ($method -eq "https") {
            $remoteUrl = (git remote get-url $Remote 2>$null).Trim()
            if (-not $remoteUrl) { throw "Could not read URL for remote '$Remote'" }
            $httpsUrl = ConvertTo-GitHubHttpsUrl $remoteUrl
            Write-Host "==> git push via HTTPS ($httpsUrl -> $Branch)..." -ForegroundColor Cyan
            Write-Host "    (uses Git Credential Manager - no SSH passphrase)" -ForegroundColor DarkGray
            git push -u $httpsUrl "HEAD:$Branch"
            if ($LASTEXITCODE -ne 0) {
                throw @"
git push failed ($LASTEXITCODE). HTTPS auth uses Windows Credential Manager.
One-time setup: git push $httpsUrl $Branch (sign in when prompted), or create a GitHub PAT
with repo scope and store it when Git prompts. To revert to SSH+passphrase, set
GitPushMethod to ssh in tools/multisheet-deploy.json
"@
            }
            return
        }

        $key = Get-MultiSheetSshKeyPath -OverrideKey $SshKeyPath
        if (-not $key) {
            throw "GitPushMethod=ssh but no SSH key found. Set SshKey in tools/multisheet-deploy.json"
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

        Write-Host "==> git push via SSH ($Remote/$Branch)..." -ForegroundColor Cyan
        git push -u $Remote $Branch
        if ($LASTEXITCODE -ne 0) { throw "git push failed with exit code $LASTEXITCODE" }

        if ($askpass -and (Test-Path $askpass)) {
            Remove-Item $askpass -Force -ErrorAction SilentlyContinue
        }
    }
    finally {
        Pop-Location
        Remove-Item Env:SSH_ASKPASS -ErrorAction SilentlyContinue
        Remove-Item Env:SSH_ASKPASS_REQUIRE -ErrorAction SilentlyContinue
        Remove-Item Env:DISPLAY -ErrorAction SilentlyContinue
        Remove-Item Env:GIT_SSH_COMMAND -ErrorAction SilentlyContinue
    }
}

function Push-MultiSheetGit {
    param(
        [string]$Root,
        [string]$Message,
        [string]$Remote = "origin",
        [string]$Branch = "main",
        [string]$SshKeyPath,
        [string]$PushMethod,
        [switch]$SkipAudit,
        [switch]$PushOnly
    )

    if (-not (Test-Path (Join-Path $Root ".git"))) {
        Write-Warning "No git repo in $Root; skipping push."
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
    try {
        if (-not $PushOnly) {
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
        }

        Invoke-DalfGitPush -Root $Root -Remote $Remote -Branch $Branch -PushMethod $PushMethod -SshKeyPath $SshKeyPath
        Write-Host "GitHub push OK" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
