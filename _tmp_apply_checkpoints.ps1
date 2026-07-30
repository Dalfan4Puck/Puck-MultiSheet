$ErrorActionPreference = "Stop"
$repo = "C:\Users\bhsda\OneDrive\Desktop\Puck Mod\Claude Puck Playground\DALF MOD LIBRARY\Dalf Multisheet"
$base = "C:\Users\bhsda\AppData\Roaming\Cursor\User\globalStorage\anysphere.cursor-retrieval\checkpoints"
$ws = "8f449923ec7a61c187d532a0ffa2151d"
$commitMs = 1785241470000   # ~6:24:30 AM Jul 28 commit
$targetMs = 1785261021527   # 0dae0921 @ 11:50 AM

function Apply-Checkpoint($id) {
    $folder = Join-Path $base $id
    $metaPath = Join-Path $folder "metadata.json"
    if (-not (Test-Path $metaPath)) { throw "Missing metadata for $id" }
    $j = Get-Content $metaPath -Raw | ConvertFrom-Json
    $time = [DateTimeOffset]::FromUnixTimeMilliseconds($j.startTrackingDateUnixMilliseconds).LocalDateTime
    $names = @()
    foreach ($f in $j.requestFiles) {
        $src = Join-Path $folder "files\$($f.fileUuid)"
        if (-not (Test-Path $src)) { throw "Missing snapshot $($f.fileUuid) in $id" }
        $dest = $f.fsPath
        $dir = Split-Path $dest -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item $src $dest -Force
        $names += Split-Path $dest -Leaf
    }
    return [PSCustomObject]@{ Id = $id; Time = $time; Files = ($names -join ", ") }
}

function Repair-EmptySnapshot($destPath, $label) {
    if ((Test-Path $destPath) -and (Get-Item $destPath).Length -gt 0) { return $false }
    Write-Host "  ! empty/missing snapshot for $label - using transcript"
    return $true
}

$ordered = Get-ChildItem $base -Directory | ForEach-Object {
    $metaPath = Join-Path $_.FullName "metadata.json"
    if (-not (Test-Path $metaPath)) { return }
    $j = Get-Content $metaPath -Raw | ConvertFrom-Json
    if ($j.workspaceId -ne $ws) { return }
    if ($j.startTrackingDateUnixMilliseconds -le $commitMs -or $j.startTrackingDateUnixMilliseconds -gt $targetMs) { return }
    [PSCustomObject]@{ Id = $_.Name; Ms = $j.startTrackingDateUnixMilliseconds; Meta = $j }
} | Sort-Object Ms

Write-Host "Applying $($ordered.Count) checkpoints from 6am through 11:50..."
Write-Host ""

$step = 0
foreach ($cp in $ordered) {
    $step++
    $info = Apply-Checkpoint $cp.Id

    $needTranscript = $false
    foreach ($f in $cp.Meta.requestFiles) {
        $leaf = Split-Path $f.fsPath -Leaf
        if ($leaf -eq 'StickIcePassThrough.cs' -and (Repair-EmptySnapshot $f.fsPath $leaf)) {
            $needTranscript = $true
        }
    }
    if ($cp.Id -eq 'e04579e6-67cc-4fd3-b8d4-1cf0c0bfd078') { $needTranscript = $true }
    if ($cp.Id -eq '2185f7c0-3e9e-435c-ab10-1f3e6cb5edc1') { $needTranscript = $true }
    if ($needTranscript) {
        python (Join-Path $repo '_tmp_extract_transcript_files.py') 2>&1 | ForEach-Object { Write-Host "  $_" }
    }

    $isCanvas = $info.Files -match '\.canvas\.tsx$' -and $info.Files -notmatch '\.cs'
    Write-Host "[$step/$($ordered.Count)] $($info.Time.ToString('h:mm tt')) $($cp.Id.Substring(0,8))... -> $($info.Files)"

    if ($isCanvas) {
        Write-Host "  (canvas only - skip build)"
        continue
    }

    Push-Location $repo
    $build = dotnet build 2>&1
    $exit = $LASTEXITCODE
    Pop-Location
    if ($exit -ne 0) {
        Write-Host "  BUILD FAILED after checkpoint $($cp.Id)"
        $build | Select-String "error CS" | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" }
        exit $exit
    }
    Write-Host "  build OK"
}

Write-Host ""
Write-Host "All checkpoints applied through 0dae0921. Final build succeeded."
