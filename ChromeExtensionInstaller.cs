using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WrenchDownloader;

/// <summary>
/// One-click-ish Chrome companion installer.
///
/// Chrome does not allow apps to silently install extensions: on stable/beta
/// only the Chrome Web Store (or the enterprise ExtensionInstallForcelist
/// policy, which needs a published extension ID) can install without manual
/// steps. Note --load-extension is NOT used: Chrome silently ignores it
/// whenever a browser instance is already running. Instead this helper:
///  1. Copies the extension to a stable per-user folder (survives app updates).
///  2. Packs it into a signed .crx with a stable key (stable extension ID).
///  3. Shows a dialog: drag the .crx onto chrome://extensions (Developer mode)
///     and confirm — the reliable two-click install.
/// </summary>
public static class ChromeExtensionInstaller
{
    private const string SourceFolderName = "chrome extension";
    private const string StagedFolderName = "chrome-extension";
    private const string CrxFileName = "WrenchDownloaderCompanion.crx";
    private const string KeyFileName = "chrome-extension-key.pem";

    public static string StagedDirectory =>
        Path.Combine(PortablePaths.DataDirectory, StagedFolderName);

    /// <summary>Stable signing key: reusing it keeps the extension ID stable across updates.</summary>
    public static string PackKeyPath =>
        Path.Combine(PortablePaths.DataDirectory, KeyFileName);

    public static string CrxPath =>
        Path.Combine(PortablePaths.DataDirectory, CrxFileName);

    /// <summary>Locate the extension source (release package layout, then dev tree).</summary>
    public static string? FindSourceDirectory()
    {
        string[] candidates =
        [
            Path.Combine(PortablePaths.PackageDirectory, SourceFolderName),
            Path.Combine(AppContext.BaseDirectory, SourceFolderName),
        ];
        foreach (string dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "manifest.json")))
                return dir;
        }

        // Dev runs (bin/Debug/...): walk up toward the repo root.
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string probe = Path.Combine(dir.FullName, SourceFolderName, "manifest.json");
                if (File.Exists(probe))
                    return Path.GetDirectoryName(probe);
                dir = dir.Parent;
            }
        }
        catch { }

        return null;
    }

    /// <summary>Copy extension files to the stable staged folder. Returns false + error on failure.</summary>
    public static bool EnsureStaged(out string stagedPath, out string? error)
    {
        stagedPath = StagedDirectory;
        error = null;
        try
        {
            string? source = FindSourceDirectory();
            if (source == null)
            {
                error = $"\"{SourceFolderName}\" folder with manifest.json was not found next to the app.";
                return false;
            }

            if (Directory.Exists(stagedPath))
                Directory.Delete(stagedPath, recursive: true);
            Directory.CreateDirectory(stagedPath);

            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(stagedPath, name));
            }

            if (!File.Exists(Path.Combine(stagedPath, "manifest.json")))
            {
                error = "manifest.json is missing from the extension folder.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Locate chrome.exe via App Paths registry, then well-known locations.</summary>
    public static string? FindChromeExe()
    {
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", false);
                    string? path = key?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        path = path.Trim('"');
                        if (File.Exists(path))
                            return path;
                    }
                }
                catch { }
            }
        }
        catch { }

        string[] wellKnown =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        ];
        string? found = wellKnown.FirstOrDefault(File.Exists);
        if (found != null)
            return found;

        // Last resort: hope chrome.exe is on PATH.
        return "chrome.exe";
    }

    /// <summary>Open the Chrome extensions page (for manual Load unpacked).</summary>
    public static bool OpenExtensionsPage(out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = FindChromeExe() ?? "chrome.exe",
                Arguments = "chrome://extensions",
                UseShellExecute = false
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Resolve a working chrome.exe path (registry, well-known dirs, PATH).</summary>
    private static string? ResolveChromeExe()
    {
        string? candidate = FindChromeExe();
        if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
            return candidate;

        try
        {
            var probe = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "chrome.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(probe);
            string output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(10000);
            string? found = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Trim('"'))
                .FirstOrDefault(File.Exists);
            if (found != null)
                return found;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Pack the staged extension into a signed .crx (stable ID via <see cref="PackKeyPath"/>).
    /// Must run off the UI thread: Chrome packing takes a few seconds.
    /// </summary>
    public static bool EnsureCrx(out string crxPath, out string? error)
    {
        crxPath = "";
        error = null;
        try
        {
            if (!EnsureStaged(out string staged, out string? stageError))
            {
                error = stageError;
                return false;
            }

            string? chrome = ResolveChromeExe();
            if (chrome == null)
            {
                error = "Google Chrome was not found on this PC.";
                return false;
            }

            // Pack with an isolated throwaway profile: a running Chrome instance
            // would otherwise swallow the --pack-extension flag.
            string profileDir = Path.Combine(Path.GetTempPath(), "WrenchPackProfile");
            try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, recursive: true); } catch { }

            string args = $"--user-data-dir=\"{profileDir}\" --no-first-run --no-default-browser-check --pack-extension=\"{staged}\"";
            if (File.Exists(PackKeyPath))
                args += $" --pack-extension-key=\"{PackKeyPath}\"";

            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = chrome,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(120000);
            }
            finally
            {
                try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, recursive: true); } catch { }
            }

            string siblingCrx = staged + ".crx";
            string siblingPem = staged + ".pem";
            if (!File.Exists(siblingCrx))
            {
                error = "Chrome could not package the extension.";
                return false;
            }

            crxPath = CrxPath;
            File.Copy(siblingCrx, crxPath, overwrite: true);
            try { File.Delete(siblingCrx); } catch { }

            if (!File.Exists(PackKeyPath) && File.Exists(siblingPem))
            {
                try { File.Move(siblingPem, PackKeyPath); } catch { }
            }
            else if (File.Exists(siblingPem))
            {
                try { File.Delete(siblingPem); } catch { }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Stage the unpacked extension (fresh copy) and reveal it in Explorer,
    /// ready for chrome://extensions → Developer mode → Load unpacked.
    /// Unpacked+Developer-mode is the flow Chrome keeps enabled (sideloaded
    /// .crx files get disabled for not coming from the Web Store).
    /// </summary>
    public static bool OpenStagedFolder(out string stagedPath, out string? error)
    {
        stagedPath = "";
        error = null;
        if (!EnsureStaged(out stagedPath, out string? stageError))
        {
            error = stageError;
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{stagedPath}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
