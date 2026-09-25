using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WrenchDownloader;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<DownloadItem> Downloads { get; } = new();
    private ExtensionBridgeServer? _bridgeServer;
#if !DEBUG
    private TrayIconHelper? _trayIcon;
    private bool _isExplicitExit;
#endif
    private static readonly string HistoryFilePath = PortablePaths.HistoryFilePath;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Wrench Downloader v26.1.2";
        // Start Maximized:
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        DownloadsListView.ItemsSource = Downloads;
        Downloads.CollectionChanged += (s, e) =>
        {
            UpdateCounts();
            SaveHistory();
        };

        // Load existing history
        LoadHistory();
        UpdateCounts();

        // Flush history immediately on window closing/closed
        Closed += (s, e) =>
        {
#if !DEBUG
            _trayIcon?.Dispose();
#endif
            SaveHistoryToFile();
        };

#if !DEBUG
        // Initialize System Tray (Release mode only)
        InitTrayIcon();

        // Handle Close button / Alt+F4
        AppWindow.Closing += (sender, args) =>
        {
            if (!_isExplicitExit && SettingsHelper.CloseToTray)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
            else
            {
                _trayIcon?.Dispose();
            }
        };
#endif

        // Start Extension Bridge Server
        _bridgeServer = new ExtensionBridgeServer(OnExtensionDownloadRequested);
        _bridgeServer.Start();
    }

#if !DEBUG
    private void InitTrayIcon()
    {
        try
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _trayIcon = new TrayIconHelper(hWnd);
            _trayIcon.OnOpenRequested += () => DispatcherQueue.TryEnqueue(RestoreAndShow);
            _trayIcon.OnSettingsRequested += () => DispatcherQueue.TryEnqueue(() =>
            {
                RestoreAndShow();
                OnSettingsClick(this, new RoutedEventArgs());
            });
            _trayIcon.OnExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);
            _trayIcon.Initialize("Wrench Downloader");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to init tray icon: {ex.Message}");
        }
    }

    public void RestoreAndShow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }
        }
        Activate();
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        SaveHistoryToFile();
        try { _bridgeServer?.Stop(); } catch { }
        AppWindow.Destroy();
        Application.Current.Exit();
    }
#endif

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFilePath))
            {
                string json = File.ReadAllText(HistoryFilePath);
                var loaded = JsonSerializer.Deserialize<List<DownloadItem>>(json);
                if (loaded != null)
                {
                    foreach (var item in loaded)
                    {
                        item.SetDispatcherQueue(DispatcherQueue);
                        if (item.Status == DownloadStatus.Downloading || item.Status == DownloadStatus.Queued)
                        {
                            item.Status = DownloadStatus.Paused;
                            item.StatusText = "Interrupted";
                        }
                        Downloads.Add(item);
                    }
                }
                // Once history.json exists (even if empty because the user removed items), do NOT scan the folder.
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load history: {ex.Message}");
        }

        // First run only (when history.json does not exist at all):
        // Scan the download folder once to populate initial files, then save history.json so it never re-scans.
        try
        {
            string folder = SettingsHelper.DownloadFolder;
            if (Directory.Exists(folder))
            {
                var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".webm", ".ts", ".mp3", ".m4a" };
                var files = Directory.GetFiles(folder)
                    .Select(f => new FileInfo(f))
                    .Where(fi => extensions.Contains(fi.Extension) && !fi.Name.EndsWith(".part") && !fi.Name.EndsWith(".ytdl"))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .Take(25);

                foreach (var fi in files)
                {
                    var item = new DownloadItem
                    {
                        Title = Path.GetFileNameWithoutExtension(fi.Name),
                        SavePath = fi.FullName,
                        Status = DownloadStatus.Completed,
                        StatusText = "Completed",
                        Progress = 100,
                        SizeText = DownloadEngine.FormatBytes(fi.Length),
                        SpeedText = "Finished",
                        CreatedAt = fi.CreationTime
                    };
                    item.SetDispatcherQueue(DispatcherQueue);
                    Downloads.Add(item);
                }
            }
            SaveHistoryToFile();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to scan download folder: {ex.Message}");
        }
    }

    private readonly object _saveLock = new();
    private System.Threading.Timer? _saveDebounceTimer;

    public void SaveHistory(bool immediate = false)
    {
        if (immediate)
        {
            lock (_saveLock)
            {
                _saveDebounceTimer?.Dispose();
                _saveDebounceTimer = null;
            }
            SaveHistoryToFile();
            return;
        }

        // Debounce to prevent repetitive synchronous disk writes freezing the UI
        lock (_saveLock)
        {
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = new System.Threading.Timer(_ =>
            {
                SaveHistoryToFile();
            }, null, 500, Timeout.Infinite);
        }
    }

    private void SaveHistoryToFile()
    {
        try
        {
            List<DownloadItem> itemsToSave;
            // Access UI collection snapshot safely
            if (DispatcherQueue.HasThreadAccess)
            {
                itemsToSave = Downloads.Take(100).ToList();
            }
            else
            {
                var tcs = new TaskCompletionSource<List<DownloadItem>>();
                DispatcherQueue.TryEnqueue(() =>
                {
                    tcs.TrySetResult(Downloads.Take(100).ToList());
                });
                if (!tcs.Task.Wait(1000)) return;
                itemsToSave = tcs.Task.Result;
            }

            string dir = Path.GetDirectoryName(HistoryFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(itemsToSave, options);
            File.WriteAllText(HistoryFilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save history: {ex.Message}");
        }
    }

    private void OnExtensionDownloadRequested((DownloadItem item, bool showPrompt) request)
    {
        var (item, promptRequestedByCaller) = request;
        bool shouldPrompt = promptRequestedByCaller || SettingsHelper.ShowDownloadDialog;

        // Must dispatch to UI thread
        DispatcherQueue.TryEnqueue(() =>
        {
            item.SetDispatcherQueue(DispatcherQueue);
            if (shouldPrompt)
            {
                var prompt = new DownloadPromptWindow(item, confirmedItem =>
                {
                    Downloads.Insert(0, confirmedItem);
                    _ = Task.Run(async () =>
                    {
                        await DownloadEngine.StartDownloadAsync(confirmedItem, confirmedItem.Cts.Token);
                        SaveHistory();
                    });
                });
                prompt.ShowAndFocus();
            }
            else
            {
                // Download immediately without confirmation window
                Downloads.Insert(0, item);
                StatusTextBlock.Text = $"Started download: {item.Title}";
                _ = Task.Run(async () =>
                {
                    await DownloadEngine.StartDownloadAsync(item, item.Cts.Token);
                    SaveHistory();
                });
            }
        });
    }

    private void OnAddDownloadClick(object sender, RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusTextBlock.Text = "Please enter a valid URL.";
            return;
        }

        string initialTitle = "";
        try
        {
            var uri = new Uri(url);
            initialTitle = Path.GetFileName(uri.AbsolutePath);
            if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || 
                uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(initialTitle) ||
                initialTitle.Equals("watch", StringComparison.OrdinalIgnoreCase) ||
                initialTitle.Equals("view_video.php", StringComparison.OrdinalIgnoreCase) ||
                initialTitle.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                initialTitle.EndsWith(".php", StringComparison.OrdinalIgnoreCase) ||
                initialTitle.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                initialTitle.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                initialTitle = "Video_Download";
            }
        }
        catch
        {
            initialTitle = "Video_Download";
        }

        var item = new DownloadItem
        {
            Url = url,
            Title = initialTitle,
            Quality = SettingsHelper.DefaultQuality,
            Status = DownloadStatus.Queued,
            StatusText = "Connecting..."
        };

        Downloads.Insert(0, item);
        UrlTextBox.Text = string.Empty;
        StatusTextBlock.Text = $"Started download: {item.Title}";
        _ = Task.Run(async () =>
        {
            await DownloadEngine.StartDownloadAsync(item, item.Cts.Token);
            SaveHistory();
        });
    }

    private void OnRemoveDownloadClick(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetItems(sender);
        if (targets.Count == 0)
        {
            StatusTextBlock.Text = "Nothing selected to remove.";
            return;
        }
        foreach (var item in targets)
        {
            try { item.Cts.Cancel(); } catch { }
            Downloads.Remove(item);
        }
        SaveHistory(immediate: true);
        StatusTextBlock.Text = $"Removed {targets.Count} item{(targets.Count == 1 ? "" : "s")}.";
    }

    private void OnRemoveSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = DownloadsListView.SelectedItems.OfType<DownloadItem>().ToList();
        if (selected.Count == 0)
        {
            StatusTextBlock.Text = "Nothing selected. Use Ctrl+Click or Shift+Click to multi-select.";
            return;
        }
        foreach (var item in selected)
        {
            try { item.Cts.Cancel(); } catch { }
            Downloads.Remove(item);
        }
        SaveHistory(immediate: true);
        StatusTextBlock.Text = $"Removed {selected.Count} selected item{(selected.Count == 1 ? "" : "s")}.";
    }

    private void OnRemoveCompletedClick(object sender, RoutedEventArgs e)
    {
        var done = Downloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
        if (done.Count == 0)
        {
            StatusTextBlock.Text = "No completed downloads to remove.";
            return;
        }
        foreach (var item in done) Downloads.Remove(item);
        SaveHistory(immediate: true);
        StatusTextBlock.Text = $"Removed {done.Count} completed download{(done.Count == 1 ? "" : "s")}.";
    }

    private void OnRemoveAllClick(object sender, RoutedEventArgs e)
    {
        if (Downloads.Count == 0)
        {
            StatusTextBlock.Text = "List is already empty.";
            return;
        }
        foreach (var item in Downloads) { try { item.Cts.Cancel(); } catch { } }
        int n = Downloads.Count;
        Downloads.Clear();
        SaveHistory(immediate: true);
        StatusTextBlock.Text = $"Cleared {n} item{(n == 1 ? "" : "s")} (active downloads cancelled).";
    }

    private void OnDownloadsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCounts();
    }

    private void OnDownloadsListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            OnRemoveSelectedClick(sender, e);
            e.Handled = true;
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
        StatusTextBlock.Text = $"Settings saved. Downloads go to {SettingsHelper.DownloadFolder}.";
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var aboutPanel = new StackPanel { Spacing = 12, MinWidth = 360 };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        headerRow.Children.Add(new TextBlock { Text = "🔧", FontSize = 28, VerticalAlignment = VerticalAlignment.Center });
        
        var titleStack = new StackPanel { Spacing = 2 };
        titleStack.Children.Add(new TextBlock { Text = "Wrench Downloader", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titleStack.Children.Add(new TextBlock { Text = "Version 26.1.2", FontSize = 13, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        headerRow.Children.Add(titleStack);
        aboutPanel.Children.Add(headerRow);

        aboutPanel.Children.Add(new TextBlock
        {
            Text = "Ultra-fast media and file download manager. Automatically detects videos and files in your browser and accelerates downloads with multi-connection parallel fragments.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var detailsStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        detailsStack.Children.Add(new TextBlock { Text = "• Developed by: Wrench Softwares", FontSize = 12 });
        detailsStack.Children.Add(new TextBlock { Text = "• Official Website & Source: https://github.com/wrenchsoftwares/wrench-downloader", FontSize = 12 });
        detailsStack.Children.Add(new TextBlock { Text = "• Features: Multi-threaded parallel acceleration, browser stream sniffer, auto-resume & video format conversion", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        aboutPanel.Children.Add(detailsStack);

        var dialog = new ContentDialog
        {
            Title = "About",
            Content = aboutPanel,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void UpdateCounts()
    {
        int selected = DownloadsListView?.SelectedItems?.OfType<DownloadItem>().Count() ?? 0;
        DownloadCountTextBlock.Text = $"{Downloads.Count} Download{(Downloads.Count == 1 ? "" : "s")} • {selected} selected";
        if (RemoveSelectedButton != null) RemoveSelectedButton.IsEnabled = selected > 0;
    }

    private List<DownloadItem> GetTargetItems(object sender)
    {
        DownloadItem? contextItem = (sender as FrameworkElement)?.DataContext as DownloadItem;
        var selected = DownloadsListView.SelectedItems.OfType<DownloadItem>().ToList();
        if (contextItem != null && selected.Contains(contextItem)) return selected;
        if (contextItem != null) return new List<DownloadItem> { contextItem };
        return selected;
    }

    private void OnTogglePauseDownloadClick(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetItems(sender);
        if (targets.Count == 0) return;

        foreach (var item in targets)
        {
            if (item.IsActive)
            {
                // Pause active download
                item.Cancel();
                item.Status = DownloadStatus.Paused;
                item.SpeedText = "Paused";
                item.StatusText = "Paused by user";
                StatusTextBlock.Text = $"Paused download: {item.Title}";
            }
            else if (item.Status == DownloadStatus.Paused || item.Status == DownloadStatus.Failed)
            {
                // Resume download
                var token = item.ResetCancellationToken();
                item.Status = DownloadStatus.Queued;
                item.SpeedText = "Resuming...";
                item.StatusText = "Connecting...";
                StatusTextBlock.Text = $"Resumed download: {item.Title}";

                _ = Task.Run(async () =>
                {
                    await DownloadEngine.StartDownloadAsync(item, token);
                    SaveHistory();
                });
            }
        }
        SaveHistory();
    }

    private void OnDeleteDownloadedFileClick(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetItems(sender);
        if (targets.Count == 0)
        {
            StatusTextBlock.Text = "Nothing selected to delete.";
            return;
        }
        int deleted = 0;
        foreach (var item in targets)
        {
            try { item.Cts.Cancel(); } catch { }
            Downloads.Remove(item);
            if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.SavePath))
            {
                try { File.Delete(item.SavePath); deleted++; } catch { }
            }
        }
        SaveHistory(immediate: true);
        StatusTextBlock.Text = $"Removed {targets.Count} item{(targets.Count == 1 ? "" : "s")}, deleted {deleted} file{(deleted == 1 ? "" : "s")} from disk.";
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.SavePath))
            {
                Process.Start(new ProcessStartInfo { FileName = item.SavePath, UseShellExecute = true });
            }
            else
            {
                StatusTextBlock.Text = "File has not finished downloading yet or does not exist.";
            }
        }
    }

    private void OnOpenContainingFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.SavePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.SavePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                OnOpenFolderClick(sender, e);
            }
        }
    }

    private void OnCopyDownloadLinkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(item.Url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            StatusTextBlock.Text = "Copied download link to clipboard.";
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = downloads,
                UseShellExecute = true
            });
        }
    }
}
