$ErrorActionPreference = "Stop"

$version = "26.4.5"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $root

$stage = Join-Path $root "artifacts\stage\$version"
$releaseDir = Join-Path $root "target\release"
$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"

if (-not (Test-Path (Join-Path $releaseDir "wrench-downloader.exe"))) {
    throw "Release binary missing. Run: cargo build --release"
}
if (-not (Test-Path $iscc)) {
    throw "Inno Setup 6 not found at $iscc"
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item (Join-Path $releaseDir "wrench-downloader.exe") $stage -Force
Copy-Item (Join-Path $root "Assets\AppIcon.ico") (Join-Path $stage "app.ico") -Force
Copy-Item (Join-Path $root "Languages") (Join-Path $stage "Languages") -Recurse -Force
Copy-Item (Join-Path $root "chrome extension") (Join-Path $stage "chrome extension") -Recurse -Force

# The source manifest is the dev build and keeps the " debug" suffix so you
# can tell it apart in chrome://extensions. Release installs drop it.
$stagedManifest = Join-Path $stage "chrome extension\manifest.json"
if (Test-Path $stagedManifest) {
    $manifest = Get-Content $stagedManifest -Raw
    $manifest = $manifest -replace '"name"\s*:\s*"Wrench Downloader Companion debug"', '"name": "Wrench Downloader Companion"'
    [System.IO.File]::WriteAllText($stagedManifest, $manifest, (New-Object System.Text.UTF8Encoding($false)))
}

Push-Location $root
try {
    & $iscc "/DMyAppVersion=$version" "installer\app.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

Get-ChildItem (Join-Path $root "artifacts\releases\$version") -Filter "*-Setup.exe" |
    Select-Object FullName, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
