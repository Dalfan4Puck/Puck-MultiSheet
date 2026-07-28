# Build FlamiePrac and push server-needed files to the dedicated Plugins folder.
#
# Usage (from this folder):
#   .\deploy-server.ps1              # build + upload to VPS + local Plugins copy
#   .\deploy-server.ps1 -SkipBuild   # upload current dist only
#   .\deploy-server.ps1 -SkipRemote  # local Plugins only
#   .\deploy-server.ps1 -SkipLocal   # VPS only
#
# Config (first found wins):
#   $env:FLAMIE_DEPLOY_CONFIG
#   %USERPROFILE%\OneDrive\Desktop\Puck Mod\Server & SQL Files\tools\flamietraining-deploy.json
#   flamietraining-deploy.json (beside this script)

param(
    [switch]$SkipBuild,
    [switch]$SkipRemote,
    [switch]$SkipLocal,
    [switch]$RestartServer,
    [string]$Configuration = "Release",
    [string]$ConfigPath
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$DistDir = Join-Path $ProjectRoot "dist"
$Csproj = Join-Path $ProjectRoot "MyMod.csproj"

function Resolve-DeployConfigPath {
    param([string]$Override)
    if ($Override -and (Test-Path $Override)) { return $Override }
    if ($env:FLAMIE_DEPLOY_CONFIG -and (Test-Path $env:FLAMIE_DEPLOY_CONFIG)) {
        return $env:FLAMIE_DEPLOY_CONFIG
    }
    $preferred = Join-Path $env:USERPROFILE "OneDrive\Desktop\Puck Mod\Server & SQL Files\tools\flamietraining-deploy.json"
    if (Test-Path $preferred) { return $preferred }
    $beside = Join-Path $ProjectRoot "flamietraining-deploy.json"
    if (Test-Path $beside) { return $beside }
    $multi = Join-Path $env:USERPROFILE "OneDrive\Desktop\Puck Mod\Server & SQL Files\tools\multisheet-deploy.json"
    if (Test-Path $multi) { return $multi }
    return $null
}

function Get-DeployConfig {
    param([string]$Path)
    $cfg = [pscustomobject]@{
        SshKey         = $null
        SshHost        = $null
        SshPort        = 22
        SshUser        = "root"
        VpsPluginDir   = "/srv/puck-download/Plugins/FlamiePrac"
        LocalPluginDir = "C:\Program Files (x86)\Steam\steamapps\common\Puck\Plugins\FlamiePrac"
    }
    if (-not $Path) { return $cfg }
    $raw = Get-Content $Path -Raw | ConvertFrom-Json
    foreach ($prop in @("SshKey", "SshHost", "SshPort", "SshUser", "VpsPluginDir", "LocalPluginDir")) {
        if ($null -ne $raw.$prop -and "$($raw.$prop)" -ne "") {
            $cfg.$prop = $raw.$prop
        }
    }
    if ($cfg.VpsPluginDir -match "workshop|3771130437|MultiSheet") {
        $cfg.VpsPluginDir = "/srv/puck-download/Plugins/FlamiePrac"
    }
    if ($cfg.LocalPluginDir -match "MultiSheet|DalfMultiSheet") {
        $cfg.LocalPluginDir = "C:\Program Files (x86)\Steam\steamapps\common\Puck\Plugins\FlamiePrac"
    }
    return $cfg
}

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name (install OpenSSH client)"
    }
}

function Get-ServerPayloadFiles {
    $names = @(
        "MyMod.dll",
        "trainingprefabs",
        "training_layout.json",
        "training_layout.example.json",
        "training_prefab_names.json",
        "SERVER_DEPLOY.md"
    )
    $files = @()
    foreach ($name in $names) {
        $path = Join-Path $DistDir $name
        if (-not (Test-Path $path)) {
            throw "Missing dist file required for server: $name"
        }
        $files += (Get-Item $path)
    }
    return $files
}

function Disable-LegacyFlamieTrainingPlugin {
    param([string]$LocalDir)
    # Old folder name still ships beside FlamiePrac on some clients — both load as IPuckPlugin
    # with the same Harmony id and fight over hive spawn / slidable sync.
    $pluginsRoot = Split-Path -Parent $LocalDir
    $legacyDll = Join-Path $pluginsRoot "FlamieTraining\MyMod.dll"
    $legacyDisabled = Join-Path $pluginsRoot "FlamieTraining\MyMod.dll.disabled"
    if (Test-Path $legacyDll) {
        if (Test-Path $legacyDisabled) { Remove-Item -LiteralPath $legacyDisabled -Force }
        Move-Item -LiteralPath $legacyDll -Destination $legacyDisabled -Force
        Write-Host "  disabled legacy Plugins\FlamieTraining\MyMod.dll (use FlamiePrac only)" -ForegroundColor Yellow
    }
}

function Install-LocalPayload {
    param([string]$LocalDir, [System.IO.FileInfo[]]$Files)
    if (-not (Test-Path $LocalDir)) {
        New-Item -ItemType Directory -Path $LocalDir -Force | Out-Null
    }
    foreach ($file in $Files) {
        $dest = Join-Path $LocalDir $file.Name
        $temp = "$dest.tmp"
        Copy-Item -LiteralPath $file.FullName -Destination $temp -Force
        Move-Item -LiteralPath $temp -Destination $dest -Force
        Write-Host ("  local: " + $file.Name) -ForegroundColor DarkGray
    }
    Disable-LegacyFlamieTrainingPlugin -LocalDir $LocalDir
}

function Publish-RemotePayload {
    param($Cfg, [System.IO.FileInfo[]]$Files)

    if (-not $Cfg.SshHost) { throw "SshHost missing from deploy config." }
    if (-not $Cfg.SshKey -or -not (Test-Path $Cfg.SshKey)) {
        throw "SshKey missing or not found: $($Cfg.SshKey)"
    }

    Assert-Command "ssh"
    Assert-Command "scp"

    $port = [int]$Cfg.SshPort
    $target = "$($Cfg.SshUser)@$($Cfg.SshHost)"
    $remoteDir = $Cfg.VpsPluginDir.TrimEnd("/")
    $sshArgs = @(
        "-i", $Cfg.SshKey,
        "-p", "$port",
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=accept-new"
    )
    $scpArgs = @(
        "-i", $Cfg.SshKey,
        "-P", "$port",
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=accept-new"
    )

    Write-Host ("==> Ensuring remote dir " + $remoteDir) -ForegroundColor Cyan
    & ssh @sshArgs $target ("mkdir -p '" + $remoteDir + "'")
    if ($LASTEXITCODE -ne 0) { throw "ssh mkdir failed ($LASTEXITCODE)" }

    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $remoteStage = "/tmp/flamietraining-deploy-" + $stamp
    & ssh @sshArgs $target ("rm -rf '" + $remoteStage + "' && mkdir -p '" + $remoteStage + "'")
    if ($LASTEXITCODE -ne 0) { throw "ssh stage mkdir failed ($LASTEXITCODE)" }

    Write-Host ("==> Uploading " + $Files.Count + " file(s) via scp...") -ForegroundColor Cyan
    $paths = @($Files | ForEach-Object { $_.FullName })
    & scp @scpArgs @paths ($target + ":" + $remoteStage + "/")
    if ($LASTEXITCODE -ne 0) { throw "scp failed ($LASTEXITCODE)" }

    Write-Host ("==> Installing into " + $remoteDir) -ForegroundColor Cyan
    $remoteCmd = "cp -f '" + $remoteStage + "'/* '" + $remoteDir + "'/ && rm -rf '" + $remoteStage + "' && ls -la '" + $remoteDir + "'"
    & ssh @sshArgs $target $remoteCmd
    if ($LASTEXITCODE -ne 0) { throw "remote install failed ($LASTEXITCODE)" }
}

$resolvedConfig = Resolve-DeployConfigPath -Override $ConfigPath
if (-not $resolvedConfig) {
    throw "No deploy config found. See flamietraining-deploy.example.json"
}
Write-Host ("Config: " + $resolvedConfig) -ForegroundColor DarkGray
$cfg = Get-DeployConfig -Path $resolvedConfig

if (-not $SkipBuild) {
    Write-Host ("==> Building " + $Configuration + "...") -ForegroundColor Cyan
    $env:DeployDir = $cfg.LocalPluginDir
    dotnet build $Csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
}
else {
    Write-Host "==> SkipBuild - using existing dist" -ForegroundColor Yellow
}

$payload = Get-ServerPayloadFiles
Write-Host "Payload:" -ForegroundColor Cyan
foreach ($f in $payload) {
    Write-Host ("  " + $f.Name + "  (" + $f.Length + " bytes)") -ForegroundColor DarkGray
}

if (-not $SkipLocal) {
    Write-Host ("==> Local Plugins: " + $cfg.LocalPluginDir) -ForegroundColor Cyan
    Install-LocalPayload -LocalDir $cfg.LocalPluginDir -Files $payload
}

if (-not $SkipRemote) {
    Write-Host ("==> Remote Plugins: " + $cfg.VpsPluginDir + " on " + $cfg.SshHost + ":" + $cfg.SshPort) -ForegroundColor Cyan
    Publish-RemotePayload -Cfg $cfg -Files $payload
}

if ($RestartServer -and -not $SkipRemote) {
    Write-Host "==> Restarting dedicated Puck (plugins load only at process start)..." -ForegroundColor Cyan
    $port = [int]$cfg.SshPort
    $target = "$($cfg.SshUser)@$($cfg.SshHost)"
    $sshArgs = @(
        "-i", $cfg.SshKey,
        "-p", "$port",
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=accept-new"
    )
    # Upload a disk script — never pkill -f from the SSH command line (it matches itself).
    $restartLocal = Join-Path $env:TEMP "flamietraining-restart-puck.sh"
    # Always restart via systemd. A second nohup start_server races PuckServer.service
    # and leaves one process bound + one failing "address already in use".
    $restartBody = @'
#!/bin/bash
set -e
cd /srv/puck-download

systemctl stop PuckServer.service || true

# Sweep any orphan nohup copies left from older deploy scripts.
while read -r pid; do
  [ -z "$pid" ] && continue
  exe=$(readlink -f "/proc/$pid/exe" 2>/dev/null || true)
  cmd=$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)
  if [ "$exe" = "/srv/puck-download/Puck" ] || echo "$cmd" | grep -q '/srv/puck-download/start_server.sh'; then
    kill -9 "$pid" 2>/dev/null || true
  fi
done < <(ps -eo pid=)

fuser -k 30609/udp 30609/tcp 2>/dev/null || true

for i in $(seq 1 40); do
  if ! pgrep -f '/srv/puck-download/Puck' >/dev/null 2>&1 && ! ss -ulpn 2>/dev/null | grep -q ':30609'; then
    break
  fi
  sleep 0.5
done
sleep 2

: > /srv/puck-download/Logs/Puck.log
systemctl start PuckServer.service
sleep 16

echo "=== systemd ==="
systemctl is-active PuckServer.service
echo "=== processes ==="
ps -eo pid,ppid,etime,cmd | grep -E '/srv/puck-download/Puck|/srv/puck-download/start_server' | grep -v grep || true
puck_count=$(pgrep -c -f '/srv/puck-download/Puck' 2>/dev/null || echo 0)
echo "Puck process count: $puck_count"
ss -ulpn 2>/dev/null | grep 30609 || echo "NOT_LISTENING"

echo "=== FlamiePrac boot ==="
grep -a -iE 'Adding plugin|FlamiePrac|Network ready|Starting training|Spawned .training|Failed to bind|Failed to start server|Slidable sync locked|hiveMotion' /srv/puck-download/Logs/Puck.log | tail -n 50 || true

if [ "$puck_count" != "1" ]; then
  echo "ERROR: expected exactly 1 Puck process, found $puck_count" >&2
  exit 1
fi
if grep -a -qiE 'Failed to bind|Failed to start server' /srv/puck-download/Logs/Puck.log; then
  echo "ERROR: server failed to bind" >&2
  exit 1
fi
if ! ss -ulpn 2>/dev/null | grep -q ':30609'; then
  echo "ERROR: not listening on 30609" >&2
  exit 1
fi
if ! grep -a -q 'Adding plugin FlamiePrac' /srv/puck-download/Logs/Puck.log; then
  echo "ERROR: FlamiePrac did not load" >&2
  exit 1
fi
if ! grep -a -q 'Starting training mode' /srv/puck-download/Logs/Puck.log; then
  echo "WARN: training mode not started yet (ice may still be loading)" >&2
fi
echo "Restart OK (single systemd instance)."
'@
    # UTF-8 without BOM — PowerShell's utf8 encoding adds a BOM that breaks #!/bin/bash.
    [System.IO.File]::WriteAllText($restartLocal, $restartBody, (New-Object System.Text.UTF8Encoding $false))
    & scp @("-i", $cfg.SshKey, "-P", "$port", "-o", "IdentitiesOnly=yes") $restartLocal ($target + ":/tmp/flamietraining-restart-puck.sh")
    if ($LASTEXITCODE -ne 0) { throw "scp restart script failed ($LASTEXITCODE)" }
    & ssh @sshArgs $target "sed -i '1s/^\xEF\xBB\xBF//;s/\r`$//' /tmp/flamietraining-restart-puck.sh; chmod +x /tmp/flamietraining-restart-puck.sh; /tmp/flamietraining-restart-puck.sh"
    if ($LASTEXITCODE -ne 0) { throw "server restart failed ($LASTEXITCODE)" }
}

Write-Host ""
Write-Host "Deploy OK." -ForegroundColor Green
if (-not $RestartServer) {
    Write-Host "IMPORTANT: restart the dedicated Puck process (or rerun with -RestartServer)." -ForegroundColor Yellow
    Write-Host "Plugins under /srv/puck-download/Plugins are only loaded at boot." -ForegroundColor Yellow
}
Write-Host "Clients need the same package under Plugins/FlamiePrac (disable old FlamieTraining folder)." -ForegroundColor Yellow
