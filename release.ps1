param(
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "WrenchDownloader.csproj"
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = $project.SelectSingleNode("/Project/PropertyGroup/Version").InnerText
$targetFramework = $project.SelectSingleNode("/Project/PropertyGroup/TargetFramework").InnerText
$assemblyVersion = "$version.0"
$releaseRoot = Join-Path $PSScriptRoot "artifacts/releases/$version"
$packageName = "WrenchDownloader-$version-$RuntimeIdentifier"
$packageDirectory = Join-Path $releaseRoot $packageName
$appDirectory = Join-Path $packageDirectory "Wrench Downloader"
$archivePath = Join-Path $releaseRoot "$packageName.zip"
$extensionSource = Join-Path $PSScriptRoot "chrome extension"
$extensionDirectory = Join-Path $packageDirectory "Chrome Companion"

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null

$publishArguments = @(
    "publish",
    $projectPath,
    "--configuration", "Release",
    "--runtime", $RuntimeIdentifier,
    "--self-contained", "true",
    "-p:PublishTrimmed=false",
    "-p:Version=$version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:InformationalVersion=$version",
    "-p:PublishDir=$appDirectory/"
)

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$appPriPath = Join-Path $PSScriptRoot "bin/Release/$targetFramework/$RuntimeIdentifier/WrenchDownloader.pri"
if (-not (Test-Path $appPriPath)) {
    throw "Release build is missing the WrenchDownloader.pri XAML resource index"
}
Copy-Item -LiteralPath $appPriPath -Destination (Join-Path $appDirectory "WrenchDownloader.pri") -Force

New-Item -ItemType Directory -Path $extensionDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $extensionSource "*") -Destination $extensionDirectory -Recurse -Force

if (-not (Test-Path (Join-Path $appDirectory "WrenchDownloader.exe"))) {
    throw "Release package is missing WrenchDownloader.exe"
}
if (-not (Test-Path (Join-Path $appDirectory "WrenchDownloader.pri"))) {
    throw "Release package is missing WrenchDownloader.pri"
}
if (-not (Test-Path (Join-Path $extensionDirectory "manifest.json"))) {
    throw "Release package is missing the Chrome extension manifest"
}

Compress-Archive -Path $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal -Force
Write-Host "Release package: $packageDirectory"
Write-Host "Release archive: $archivePath"