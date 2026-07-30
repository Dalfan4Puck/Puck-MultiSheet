$ErrorActionPreference = "Stop"
$repo = "C:\Users\bhsda\OneDrive\Desktop\Puck Mod\Claude Puck Playground\DALF MOD LIBRARY\Dalf Multisheet"
$base = "C:\Users\bhsda\AppData\Roaming\Cursor\User\globalStorage\anysphere.cursor-retrieval\checkpoints"
$ws = "8f449923ec7a61c187d532a0ffa2151d"
$commitMs = 1785241470000
$targetMs = 1785261021527
$skipUntilAfter = "1c78a9cf-8118-415f-bf1c-076f38296a1c"

function Apply-Checkpoint($id) {
    $folder = Join-Path $base $id
    $j = Get-Content (Join-Path $folder "metadata.json") -Raw | ConvertFrom-Json
    $time = [DateTimeOffset]::FromUnixTimeMilliseconds($j.startTrackingDateUnixMilliseconds).LocalDateTime
    $names = @()
    foreach ($f in $j.requestFiles) {
        Copy-Item (Join-Path $folder "files\$($f.fileUuid)") $f.fsPath -Force
        $names += Split-Path $f.fsPath -Leaf
    }
    return [PSCustomObject]@{ Id = $id; Time = $time; Files = ($names -join ", ") }
}

$ordered = Get-ChildItem $base -Directory | ForEach-Object {
    $metaPath = Join-Path $_.FullName "metadata.json"
    if (-not (Test-Path $metaPath)) { return }
    $j = Get-Content $metaPath -Raw | ConvertFrom-Json
    if ($j.workspaceId -ne $ws) { return }
    if ($j.startTrackingDateUnixMilliseconds -le $commitMs -or $j.startTrackingDateUnixMilliseconds -gt $targetMs) { return }
    [PSCustomObject]@{ Id = $_.Name; Ms = $j.startTrackingDateUnixMilliseconds }
} | Sort-Object Ms

$skip = $true
$step = 0
foreach ($cp in $ordered) {
    if ($skip) {
        if ($cp.Id -eq $skipUntilAfter) { $skip = $false }
        continue
    }
    $step++
    $info = Apply-Checkpoint $cp.Id
    $isCanvas = $info.Files -match '\.canvas\.tsx$' -and $info.Files -notmatch '\.cs'
    Write-Host "[$step] $($info.Time.ToString('h:mm tt')) $($cp.Id.Substring(0,8))... -> $($info.Files)"
    if ($isCanvas) { Write-Host "  (canvas only)"; continue }
    Push-Location $repo
    dotnet build 2>&1 | Out-Null
    $exit = $LASTEXITCODE
    Pop-Location
    if ($exit -ne 0) {
        Write-Host "  BUILD FAILED"
        Push-Location $repo; dotnet build 2>&1 | Select-String "error CS" | Select-Object -First 12; Pop-Location
        exit 1
    }
    Write-Host "  build OK"
}
Write-Host "Done."
