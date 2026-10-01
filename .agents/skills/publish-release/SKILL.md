---
name: publish-release
description: >-
  Automates the complete release process for Wrench Downloader: publishes the
  version branch to GitHub, compiles the Inno Setup installer (Setup.exe), publishes
  the GitHub Release with the installer asset, and bumps the version across all 7
  project manifests on main. Use whenever asked to release, publish, create an
  installer, or bump version for a release.
---

# Wrench Downloader Release & Publishing Skill

When the user asks to **release a version**, **publish a branch + installer**, or **publish a release and bump version**, use the automated release publisher script: [`publish-release.ps1`](file:///g:/megacloud/projects/github/wrench%20downloader/publish-release.ps1).

---

## 1. Single-Command Release

Run the script directly via PowerShell:

```powershell
# Standard release:
# 1. Takes current version from WrenchDownloader.csproj
# 2. Pushes release branch <version> to origin
# 3. Compiles WrenchDownloader-<version>-Setup.exe via Inno Setup
# 4. Creates GitHub release & uploads installer
# 5. Automatically bumps all 7 manifest files to <next_version> on main and pushes
.\publish-release.ps1

# With custom next version and commit message:
.\publish-release.ps1 -CommitMessage "Add torrent & magnet support" -NextVersion "26.3.1"

# Skip building if installer is already built in artifacts/releases/<version>/:
.\publish-release.ps1 -SkipBuild -NextVersion "26.3.1"
```

---

## 2. Key Release Rules & Behaviors

1. **Clean Process**:
   Always close running `WrenchDownloader.exe` instances before publishing so binaries are never locked.
2. **7 Synchronized Files**:
   Every version bump must synchronize across all 7 files:
   - `WrenchDownloader.csproj` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`)
   - `chrome extension/manifest.json` (`version`)
   - `installer/app.iss` (`MyAppVersion`)
   - `Package.appxmanifest` (`Identity Version`)
   - `MainWindow.xaml` (`vX.Y.Z` badge)
   - `MainWindow.xaml.cs` (`main.version` and `_baseTitle`)
   - `README.md` (headers, artifact paths, manifest references)
3. **Standing Repo Rules (`AGENTS.md`)**:
   - Zero porn/NSFW mentions anywhere in code, comments, commit messages, or release notes.
   - Installer is primary distribution (`WrenchDownloader-<version>-Setup.exe`).
   - GitHub Releases are tagged `<version>` with the installer uploaded via `gh release`.
