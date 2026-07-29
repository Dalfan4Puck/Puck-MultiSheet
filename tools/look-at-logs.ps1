# Collect the latest ServerDaddy session logs for MultiSheet debugging.
# Usage:
#   .\tools\look-at-logs.ps1
#   .\tools\look-at-logs.ps1 -SessionStamp 20260729_031535
#   .\tools\look-at-logs.ps1 -SkipFetch   # use already-downloaded files only
#
# Finds:
#   - 5 server NetworkPerf files (*_servermods.log/csv, *_server.csv, *_clients.csv, *_diagnosis.log)
#   - Server Puck.log
#   - Client Puck.log
#   - Client ServerDaddy logs (*_clientmods.log/csv)

param(
    [string]$SessionStamp,
    [string]$ServerLogsDir = "/srv/puck-download/Logs",
    [string]$ClientPuckLog = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Puck\Logs\Puck.log",
    [string]$ClientServerDaddyDir = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Puck\Logs\ServerDaddyClientLogs",
    [string]$ScpCacheDir,
    [switch]$SkipFetch,
    [switch]$OpenFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "git-push-common.ps1")

function Get-LatestSessionStamp {
    param([string[]]$Names)
    $best = $null
    $bestKey = [int64]0
    foreach ($name in $Names) {
        if ($name -match "_(\d{8}_\d{6})_ServerDaddy_") {
            $key = [int64]($Matches[1] -replace "_", "")
            if ($key -gt $bestKey) {
                $bestKey = $key
                $best = $Matches[1]
            }
        }
    }
    return $best
}

function Invoke-ScpFetch {
    param(
        [string]$RemotePath,
        [string]$LocalPath,
        [string]$SshKey,
        [string]$SshHost,
        [int]$SshPort,
        [string]$SshUser,
        [string]$AskPassScript
    )
    $dir = Split-Path $LocalPath -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    if (Test-Path $LocalPath) {
        return
    }
    $env:SSH_ASKPASS = $AskPassScript
    $env:SSH_ASKPASS_REQUIRE = "force"
    $env:DISPLAY = "1"
    scp -i $SshKey -P $SshPort -o IdentitiesOnly=yes "${SshUser}@${SshHost}:$RemotePath" $LocalPath | Out-Null
}

function Copy-IfExists {
    param([string]$Source, [string]$DestDir, [string]$Label)
    if (-not (Test-Path $Source)) {
        Write-Host "  missing $Label : $Source" -ForegroundColor DarkYellow
        return $null
    }
    $name = Split-Path $Source -Leaf
    $dest = Join-Path $DestDir $name
    Copy-Item -LiteralPath $Source -Destination $dest -Force
    return $dest
}

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $ScpCacheDir) {
    $ScpCacheDir = Join-Path $repoRoot "_log_review"
}

$serverNetworkPerfRemote = "$ServerLogsDir/NetworkPerf"
$serverPuckRemote = "$ServerLogsDir/Puck.log"
$cacheNetworkPerf = Join-Path $ScpCacheDir "NetworkPerf"
$cacheServerPuck = Join-Path $ScpCacheDir "Puck.server.log"

if (-not $SkipFetch) {
    $cfgPath = Get-MultiSheetDeployConfigPath
    $sshKey = Get-MultiSheetSshKeyPath
    if (-not $sshKey) {
        Write-Warning "No SSH key - skipping server fetch. Pass -SkipFetch or set SshKey in multisheet-deploy.json."
    }
    else {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        $sshHost = $cfg.SshHost
        $sshPort = [int]$cfg.SshPort
        $sshUser = $cfg.SshUser
        if (-not (Test-Path $cacheNetworkPerf)) {
            New-Item -ItemType Directory -Path $cacheNetworkPerf -Force | Out-Null
        }

        $passFile = Join-Path (Split-Path $sshKey -Parent) "tools\github_ssh_passphrase.txt"
        $askpass = Join-Path $env:TEMP "look-at-logs-askpass-$PID.cmd"
        if (Test-Path $passFile) {
            $pass = (Get-Content $passFile -First 1).Trim()
            [System.IO.File]::WriteAllText($askpass, "@echo $pass")
        }

        Write-Host "==> Listing server NetworkPerf sessions..." -ForegroundColor Cyan
        if ($askpass) {
            $env:SSH_ASKPASS = $askpass
            $env:SSH_ASKPASS_REQUIRE = "force"
            $env:DISPLAY = "1"
        }
        $remoteListing = ssh -i $sshKey -p $sshPort -o IdentitiesOnly=yes "${sshUser}@${sshHost}" "ls -1 $serverNetworkPerfRemote 2>/dev/null" 2>$null
        $remoteNames = @($remoteListing -split "`n" | Where-Object { $_ -match "ServerDaddy" })
        if (-not $SessionStamp) {
            $SessionStamp = Get-LatestSessionStamp -Names $remoteNames
        }
        if (-not $SessionStamp) {
            throw "Could not determine latest ServerDaddy session stamp on server."
        }

        Write-Host "==> Fetching session $SessionStamp from server..." -ForegroundColor Cyan
        foreach ($name in $remoteNames) {
            if ($name -notmatch "_${SessionStamp}_ServerDaddy_") { continue }
            Invoke-ScpFetch -RemotePath "$serverNetworkPerfRemote/$name" `
                -LocalPath (Join-Path $cacheNetworkPerf $name) `
                -SshKey $sshKey -SshHost $sshHost -SshPort $sshPort -SshUser $sshUser -AskPassScript $askpass
        }
        Invoke-ScpFetch -RemotePath $serverPuckRemote -LocalPath $cacheServerPuck `
            -SshKey $sshKey -SshHost $sshHost -SshPort $sshPort -SshUser $sshUser -AskPassScript $askpass

        if ($askpass -and (Test-Path $askpass)) {
            Remove-Item $askpass -Force -ErrorAction SilentlyContinue
        }
        Remove-Item Env:SSH_ASKPASS, Env:SSH_ASKPASS_REQUIRE, Env:DISPLAY -ErrorAction SilentlyContinue
    }
}

if (-not $SessionStamp) {
    $localNames = @()
    if (Test-Path $cacheNetworkPerf) {
        $localNames += Get-ChildItem $cacheNetworkPerf -File | ForEach-Object { $_.Name }
    }
    if (Test-Path $ClientServerDaddyDir) {
        $localNames += Get-ChildItem $ClientServerDaddyDir -File | ForEach-Object { $_.Name }
    }
    $SessionStamp = Get-LatestSessionStamp -Names $localNames
}
if (-not $SessionStamp) {
    throw "No ServerDaddy session stamp found. Fetch from server or pass -SessionStamp."
}

$outDir = Join-Path $ScpCacheDir "session_$SessionStamp"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "==> Assembling review bundle: $outDir" -ForegroundColor Cyan
Write-Host "Session stamp: $SessionStamp"

$serverSuffixes = @(
    "servermods.log",
    "servermods.csv",
    "server.csv",
    "clients.csv",
    "diagnosis.log"
)

$bundle = @()
foreach ($suffix in $serverSuffixes) {
    $pattern = "*_${SessionStamp}_ServerDaddy_${suffix}"
    $src = Get-ChildItem $cacheNetworkPerf -Filter $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($src) {
        $dest = Copy-IfExists -Source $src.FullName -DestDir $outDir -Label "server $suffix"
        if ($dest) { $bundle += $dest }
    }
    else {
        Write-Host "  missing server file: $pattern" -ForegroundColor DarkYellow
    }
}

$serverPuckLocal = Copy-IfExists -Source $cacheServerPuck -DestDir $outDir -Label "server Puck.log"
if ($serverPuckLocal) { $bundle += $serverPuckLocal }

if (Test-Path $ClientPuckLog) {
    $clientPuckDest = Join-Path $outDir "client_Puck.log"
    Copy-Item -LiteralPath $ClientPuckLog -Destination $clientPuckDest -Force
    $bundle += $clientPuckDest
}
else {
    Write-Host "  missing client Puck.log: $ClientPuckLog" -ForegroundColor DarkYellow
}

foreach ($suffix in @("clientmods.log", "clientmods.csv")) {
    $pattern = "*_${SessionStamp}_ServerDaddy_${suffix}"
    $src = Get-ChildItem $ClientServerDaddyDir -Filter $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $src) {
        # Client stamp often differs from server UTC bucket — take latest Dalf_MultiSheet client file.
        $src = Get-ChildItem $ClientServerDaddyDir -Filter "Dalf_MultiSheet_*_ServerDaddy_${suffix}" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($src) {
            Write-Host "  using latest client $suffix : $($src.Name)" -ForegroundColor DarkCyan
        }
    }
    if ($src) {
        $dest = Copy-IfExists -Source $src.FullName -DestDir $outDir -Label "client $suffix"
        if ($dest) { $bundle += $dest }
    }
    else {
        Write-Host "  missing client file: $pattern" -ForegroundColor DarkYellow
    }
}

$grepPattern = "Cleared PHL|Spawning PHL|Slidable obstacle ready|Slidable sync broadcasting|Slidable pose miss|Client visuals missing|Snapshot deferred|tools changed|rink-strip|Configured .* slidable|DestroyFor|no spawn records"
$summaryPath = Join-Path $outDir "_summary.txt"
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("Session: $SessionStamp")
$summary.Add("Review folder: $outDir")
$summary.Add("")
$summary.Add("=== FlamiePrac / strip vote highlights ===")

foreach ($file in $bundle) {
    if (-not (Test-Path $file)) { continue }
    $hits = Select-String -Path $file -Pattern $grepPattern -SimpleMatch:$false -ErrorAction SilentlyContinue
    if ($hits) {
        $summary.Add('')
        $leaf = Split-Path $file -Leaf
        $summary.Add(('--- {0} ({1} matches) ---' -f $leaf, $hits.Count))
        foreach ($hit in $hits) {
            $summary.Add($hit.Line)
        }
    }
}

$summaryText = ($summary -join [Environment]::NewLine)
Set-Content -Path $summaryPath -Value $summaryText -Encoding UTF8

Write-Host ""
Write-Host $summaryText
Write-Host ""
Write-Host "Summary written to: $summaryPath" -ForegroundColor Green
Write-Host "Bundle files: $($bundle.Count)" -ForegroundColor Green

if ($OpenFolder) {
    Start-Process explorer.exe $outDir
}
