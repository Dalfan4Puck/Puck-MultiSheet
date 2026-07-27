# Pack FlamieTraining for a blank dedicated server (+ matching clients).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$stamp = Get-Date -Format "yyyyMMdd-HHmm"
$zipName = "FlamiePrac-blank-$stamp.zip"
$zipPath = Join-Path $root $zipName

Write-Host "Building Release..."
dotnet build -c Release (Join-Path $root "MyMod.csproj")
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$required = @(
    "MyMod.dll",
    "trainingprefabs",
    "training_layout.json",
    "training_layout.example.json",
    "training_prefab_names.json",
    "SERVER_DEPLOY.md"
)

foreach ($name in $required) {
    $path = Join-Path $dist $name
    if (-not (Test-Path $path)) {
        throw "Missing from dist: $name"
    }
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$stage = Join-Path $env:TEMP "FlamieTraining-pack-$stamp"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item (Join-Path $dist "*") $stage -Recurse -Force
$localSongs = "C:\Program Files (x86)\Steam\steamapps\common\Puck\Plugins\FlamiePrac\RadioSongs"
if (Test-Path $localSongs) {
    Copy-Item $localSongs (Join-Path $stage "RadioSongs") -Recurse -Force
}

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Ready: $zipPath"
Write-Host "Extract to Puck/Plugins/FlamiePrac/ on server AND clients"
Get-ChildItem $dist | Format-Table Name, Length -AutoSize
