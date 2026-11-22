$ErrorActionPreference = "Stop"

$version = "26.4.0"
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
Copy-Item (Join-Path $root "Languages") (Join-Path $stage "Languages") -Recurse -Force
Copy-Item (Join-Path $root "chrome extension") (Join-Path $stage "chrome extension") -Recurse -Force

Push-Location $root
try {
    & $iscc "/DMyAppVersion=$version" "installer\app.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

Get-ChildItem (Join-Path $root "artifacts\releases\$version") -Filter "*-Setup.exe" |
    Select-Object FullName, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
