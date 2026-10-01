param(
    [string]$Version,
    [string]$NextVersion,
    [string]$Notes,
    [string]$CommitMessage,
    [switch]$SkipBump,
    [switch]$SkipBuild
)

<#
.SYNOPSIS
    Automated one-click release publisher for Wrench Downloader.
.DESCRIPTION
    1. Reads or validates the current release version from WrenchDownloader.csproj.
    2. If working directory has unstaged/uncommitted changes and -CommitMessage is given, commits them.
    3. Creates and pushes the version branch (<Version>) to origin.
    4. Compiles the Inno Setup installer via release-installer.ps1 (producing Setup.exe).
    5. Creates or updates the GitHub Release tagged <Version> and uploads the Setup.exe asset.
    6. Automatically increments and bumps the version across all 7 project manifests/files.
    7. Commits the version bump and pushes main to origin.
.EXAMPLE
    .\publish-release.ps1
    .\publish-release.ps1 -CommitMessage "Release 26.3.0 features" -NextVersion "26.3.1"
#>

$ErrorActionPreference = "Stop"

function Get-ProjectVersion {
    param([string]$CsprojPath)
    [xml]$xml = Get-Content -LiteralPath $CsprojPath -Raw
    return $xml.SelectSingleNode("/Project/PropertyGroup/Version").InnerText.Trim()
}

function Get-NextPatchVersion {
    param([string]$CurrentVer)
    $parts = $CurrentVer.Split('.')
    if ($parts.Length -ge 3 -and [int]::TryParse($parts[-1], [ref]$null)) {
        $parts[-1] = ([int]$parts[-1] + 1).ToString()
        return ($parts -join '.')
    }
    return "$CurrentVer.1"
}

function Update-FileContent {
    param(
        [string]$FilePath,
        [scriptblock]$Transform
    )
    if (-not (Test-Path -LiteralPath $FilePath)) {
        Write-Warning "File not found: $FilePath"
        return
    }
    $raw = Get-Content -LiteralPath $FilePath -Raw
    $updated = & $Transform $raw
    if ($updated -ne $raw) {
        Set-Content -LiteralPath $FilePath -Value $updated -NoNewline -Encoding utf8
        Write-Host "  Updated: $FilePath" -ForegroundColor DarkGray
    }
}

function Set-ProjectVersionAcrossAllFiles {
    param(
        [string]$OldVer,
        [string]$NewVer,
        [string]$RootDir
    )
    Write-Host "Bumping project version: $OldVer -> $NewVer across all manifests..." -ForegroundColor Cyan

    # 1. WrenchDownloader.csproj
    $csproj = Join-Path $RootDir "WrenchDownloader.csproj"
    Update-FileContent $csproj {
        param($text)
        $text = $text -replace "<Version>$([regex]::Escape($OldVer))</Version>", "<Version>$NewVer</Version>"
        $text = $text -replace "<AssemblyVersion>$([regex]::Escape($OldVer))(\.\d+)?</AssemblyVersion>", "<AssemblyVersion>$NewVer.0</AssemblyVersion>"
        $text = $text -replace "<FileVersion>$([regex]::Escape($OldVer))(\.\d+)?</FileVersion>", "<FileVersion>$NewVer.0</FileVersion>"
        $text = $text -replace "<InformationalVersion>$([regex]::Escape($OldVer))</InformationalVersion>", "<InformationalVersion>$NewVer</InformationalVersion>"
        return $text
    }

    # 2. Chrome Extension manifest.json
    $extManifest = Join-Path $RootDir "chrome extension\manifest.json"
    Update-FileContent $extManifest {
        param($text)
        return $text -replace '("version":\s*")' + [regex]::Escape($OldVer) + '(")', "`$1$NewVer`$2"
    }

    # 3. Inno Setup app.iss
    $appIss = Join-Path $RootDir "installer\app.iss"
    Update-FileContent $appIss {
        param($text)
        return $text -replace '(#define\s+MyAppVersion\s+")' + [regex]::Escape($OldVer) + '(")', "`$1$NewVer`$2"
    }

    # 4. Package.appxmanifest
    $appx = Join-Path $RootDir "Package.appxmanifest"
    Update-FileContent $appx {
        param($text)
        return $text -replace '(Version=")' + [regex]::Escape($OldVer) + '(\.\d+)?"', "`$1$NewVer.0`""
    }

    # 5. MainWindow.xaml
    $mainXaml = Join-Path $RootDir "MainWindow.xaml"
    Update-FileContent $mainXaml {
        param($text)
        return $text -replace '(<TextBlock\s+Text="v)' + [regex]::Escape($OldVer) + '(")', "`$1$NewVer`$2"
    }

    # 6. MainWindow.xaml.cs
    $mainCs = Join-Path $RootDir "MainWindow.xaml.cs"
    Update-FileContent $mainCs {
        param($text)
        $text = $text -replace '("main\.version",\s*")' + [regex]::Escape($OldVer) + '(")', "`$1$NewVer`$2"
        $text = $text -replace '(\$"{AppLocalization\.Get\("app\.title"\)}\s+)' + [regex]::Escape($OldVer) + '(";)', "`$1$NewVer`$2"
        return $text
    }

    # 7. README.md
    $readme = Join-Path $RootDir "README.md"
    Update-FileContent $readme {
        param($text)
        $text = $text -replace '(#\s*🔧\s*Wrench Downloader\s+v)' + [regex]::Escape($OldVer), "`$1$NewVer"
        $text = $text -replace [regex]::Escape("WrenchDownloader-$OldVer-Setup.exe"), "WrenchDownloader-$NewVer-Setup.exe"
        $text = $text -replace [regex]::Escape("WrenchDownloader-$OldVer-win-x64.zip"), "WrenchDownloader-$NewVer-win-x64.zip"
        $text = $text -replace [regex]::Escape("artifacts/releases/$OldVer/"), "artifacts/releases/$NewVer/"
        $text = $text -replace [regex]::Escape("manifest (v$OldVer)"), "manifest (v$NewVer)"
        return $text
    }
}

$rootDir = $PSScriptRoot
$csprojPath = Join-Path $rootDir "WrenchDownloader.csproj"

# Ensure running app is stopped to prevent locked binaries
Get-Process WrenchDownloader -ErrorAction SilentlyContinue | Stop-Process -Force

# 1. Resolve current release version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion -CsprojPath $csprojPath
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Unable to determine current project version from $csprojPath"
}

if ([string]::IsNullOrWhiteSpace($NextVersion)) {
    $NextVersion = Get-NextPatchVersion -CurrentVer $Version
}

Write-Host "==========================================" -ForegroundColor Green
Write-Host " Wrench Downloader Release Publisher" -ForegroundColor Green
Write-Host " Releasing Version : $Version" -ForegroundColor Yellow
Write-Host " Next Dev Version  : $NextVersion" -ForegroundColor Yellow
Write-Host "==========================================" -ForegroundColor Green

# 2. Handle dirty git state if commit message supplied
$status = (git status --porcelain)
if ($status) {
    if (-not [string]::IsNullOrWhiteSpace($CommitMessage)) {
        Write-Host "Staging and committing working changes: $CommitMessage..." -ForegroundColor Cyan
        git add -A
        git commit -m "$CommitMessage"
        if ($LASTEXITCODE -ne 0) { throw "Git commit failed." }
    } else {
        Write-Warning "Working tree has uncommitted changes. These will be included in the release branch if staged, or left unstaged."
        Write-Warning "Use -CommitMessage '...' to auto-commit before releasing."
    }
}

# 3. Create or update the release branch and push to origin
Write-Host "Publishing branch '$Version' to origin..." -ForegroundColor Cyan
$existingBranch = (git branch --list $Version)
if (-not $existingBranch) {
    git branch $Version
}
git push -u origin $Version --force
if ($LASTEXITCODE -ne 0) { throw "Failed to push branch $Version to origin." }

# 4. Build the installer if not skipped
$setupPath = Join-Path $rootDir "artifacts\releases\$Version\WrenchDownloader-$Version-Setup.exe"
if (-not $SkipBuild) {
    Write-Host "Building installer with Inno Setup..." -ForegroundColor Cyan
    $releaseInstallerScript = Join-Path $rootDir "release-installer.ps1"
    & pwsh -File $releaseInstallerScript
    if ($LASTEXITCODE -ne 0) {
        # Fallback to standard powershell if pwsh not in PATH
        & powershell -File $releaseInstallerScript
        if ($LASTEXITCODE -ne 0) { throw "release-installer.ps1 failed." }
    }
}

if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Expected installer was not found at: $setupPath"
}

# 5. Create or update GitHub Release
Write-Host "Publishing GitHub Release $Version..." -ForegroundColor Cyan
if ([string]::IsNullOrWhiteSpace($Notes)) {
    $recentLog = (git log -n 5 --oneline) -join "`n"
    $Notes = "## Wrench Downloader $Version`n`n### Changes`n$recentLog`n`n### Installation`nRun ``WrenchDownloader-$Version-Setup.exe`` to install."
}

$releaseExists = $false
try {
    gh release view $Version --json tagName | Out-Null
    $releaseExists = $true
} catch {
    $releaseExists = $false
}

if ($releaseExists) {
    Write-Host "Release $Version exists, uploading Setup.exe asset..." -ForegroundColor Cyan
    gh release upload $Version "$setupPath" --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed." }
} else {
    Write-Host "Creating new GitHub Release $Version..." -ForegroundColor Cyan
    gh release create $Version "$setupPath" --target $Version --title "Wrench Downloader $Version" --notes "$Notes"
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}

Write-Host "GitHub Release $Version published successfully!" -ForegroundColor Green

# 6. Bump to NextVersion on main
if (-not $SkipBump) {
    Write-Host "Switching to main branch to bump version to $NextVersion..." -ForegroundColor Cyan
    git checkout main
    if ($LASTEXITCODE -ne 0) { throw "git checkout main failed." }

    Set-ProjectVersionAcrossAllFiles -OldVer $Version -NewVer $NextVersion -RootDir $rootDir

    git add -A
    git commit -m "Bump version to $NextVersion across all project manifests and components"
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Pushing bumped main branch to origin..." -ForegroundColor Cyan
        git push origin main
        if ($LASTEXITCODE -ne 0) { throw "git push origin main failed." }
    }
    Write-Host "Main branch successfully updated to $NextVersion!" -ForegroundColor Green
}

Write-Host "==========================================" -ForegroundColor Green
Write-Host " Release $Version complete! Dev is at $NextVersion." -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
