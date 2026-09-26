param(
    [string]$RuntimeIdentifier = "win-x64"
)

<#
.SYNOPSIS
    Builds the Wrench Downloader Setup.exe installer (primary 26.2 distribution).
.DESCRIPTION
    Publishes the self-contained app, stages it WITHOUT portable.mode
    (installed mode => settings/history live in %LOCALAPPDATA%\WrenchDownloader),
    bundles the Chrome companion extension, then compiles installer/app.iss
    with Inno Setup 6 (ISCC.exe must be installed).
    The portable ZIP remains available via release.ps1 as a secondary artifact.
#>

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "WrenchDownloader.csproj"
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = $project.SelectSingleNode("/Project/PropertyGroup/Version").InnerText
$targetFramework = $project.SelectSingleNode("/Project/PropertyGroup/TargetFramework").InnerText
$stageDirectory = Join-Path $PSScriptRoot "artifacts/stage/$version"
$extensionSource = Join-Path $PSScriptRoot "chrome extension"
$issScript = Join-Path $PSScriptRoot "installer/app.iss"
$setupPath = Join-Path $PSScriptRoot "artifacts/releases/$version/WrenchDownloader-$version-Setup.exe"

Write-Host "Publishing $version ($RuntimeIdentifier)..." -ForegroundColor Cyan
& dotnet build $projectPath --configuration Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build (Release) failed" }

& dotnet publish $projectPath --configuration Release --runtime $RuntimeIdentifier `
    --self-contained true "-p:PublishTrimmed=false" "-p:Version=$version" `
    "-p:AssemblyVersion=$version.0" "-p:FileVersion=$version.0" `
    "-p:InformationalVersion=$version" "-p:PublishDir=$stageDirectory/"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$appPriPath = Join-Path $PSScriptRoot "bin/Release/$targetFramework/$RuntimeIdentifier/WrenchDownloader.pri"
if (-not (Test-Path $appPriPath)) { throw "Release build is missing the WrenchDownloader.pri XAML resource index" }
Copy-Item -LiteralPath $appPriPath -Destination (Join-Path $stageDirectory "WrenchDownloader.pri") -Force

Write-Host "Staging Chrome extension..." -ForegroundColor Cyan
$stagedExtension = Join-Path $stageDirectory "chrome extension"
New-Item -ItemType Directory -Path $stagedExtension -Force | Out-Null
Copy-Item -Path (Join-Path $extensionSource "*") -Destination $stagedExtension -Recurse -Force

# Installed mode must NOT contain the portable marker or the portable launcher.
$strayMarker = Join-Path $stageDirectory "portable.mode"
if (Test-Path $strayMarker) { Remove-Item -LiteralPath $strayMarker -Force }

$exePath = Join-Path $stageDirectory "WrenchDownloader.exe"
if (-not (Test-Path $exePath)) { throw "Stage is missing WrenchDownloader.exe" }
if (-not (Test-Path (Join-Path $stagedExtension "manifest.json"))) { throw "Stage is missing the Chrome extension manifest" }

Write-Host "Compiling installer..." -ForegroundColor Cyan
$iscc = $null
foreach ($candidate in @(
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}
if (-not $iscc) {
    try { $iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source } catch { }
}
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php and re-run."
}

& $iscc "/DMyAppVersion=$version" $issScript
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

if (-not (Test-Path $setupPath)) { throw "Expected installer was not produced: $setupPath" }
Write-Host "Installer: $setupPath" -ForegroundColor Green
