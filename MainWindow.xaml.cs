using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WrenchDownloader;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<DownloadItem> Downloads { get; } = new();
    private ExtensionBridgeServer? _bridgeServer;
    private readonly List<DownloadItem> _waitingDownloads = new();
    private readonly HashSet<string> _activeDownloads = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _downloadQueueTimer;
    private bool _downloadQueuePaused;
    private string? _lastClipboardUrl;
    private bool _clipboardMonitoringAttached;
    private SettingsWindow? _settingsWindow;
    private readonly Dictionary<string, int> _retryCounts = new(StringComparer.Ordinal);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _taskbarTimer;
    private IntPtr _taskbarHwnd = IntPtr.Zero;
    private string _baseTitle = "Wrench Downloader";
#if !DEBUG
    private TrayIconHelper? _trayIcon;
    private bool _isExplicitExit;
#endif
    private static readonly string HistoryFilePath = PortablePaths.HistoryFilePath;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLocalization();
        ThemeHelper.ApplyTheme(this);
        LoadAppLogo();
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch { }
        // Start Maximized:
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        DownloadsListView.ItemsSource = Downloads;
        DownloadsListView.ContainerContentChanging += OnDownloadsContainerContentChanging;
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
            if (_clipboardMonitoringAttached)
            Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= OnClipboardContentChanged;
            try { _taskbarTimer?.Stop(); } catch { }
            TaskbarProgress.Reset(_taskbarHwnd);
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
        UpdateClipboardMonitoring();

        // Prefetch the aria2c multi-connection engine in the background so
        // direct downloads run at full speed from the first download on.
        _ = Task.Run(async () => { try { await Aria2cHelper.EnsureAria2cAsync(); } catch { } });

        _downloadQueueTimer = DispatcherQueue.CreateTimer();
        _downloadQueueTimer.Interval = TimeSpan.FromSeconds(10);
        _downloadQueueTimer.Tick += (_, _) => PumpDownloadQueue();
        _downloadQueueTimer.Start();

        // Mirror aggregate download progress onto the Windows taskbar button.
        try { _taskbarHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); } catch { }
        _taskbarTimer = DispatcherQueue.CreateTimer();
        _taskbarTimer.Interval = TimeSpan.FromMilliseconds(500);
        _taskbarTimer.Tick += (_, _) => UpdateTaskbarProgress();
        _taskbarTimer.Start();
    }

    private void UpdateTaskbarProgress()
    {
        try
        {
            if (_taskbarHwnd == IntPtr.Zero) return;
            var pending = Downloads
                .Where(d => d.Status == DownloadStatus.Downloading ||
                            d.Status == DownloadStatus.Queued ||
                            d.Status == DownloadStatus.Paused)
                .ToList();
            if (pending.Count > 0)
            {
                double avg = pending.Average(d => Math.Clamp(d.Progress, 0, 100));
                bool paused = _downloadQueuePaused ||
                              pending.All(d => d.Status == DownloadStatus.Paused);
                TaskbarProgress.Update(_taskbarHwnd, (ulong)(avg * 10), 1000,
                    paused ? TaskbarProgress.TaskbarState.Paused
                           : TaskbarProgress.TaskbarState.Normal);
                try { Title = $"{_baseTitle} — {avg:F0}%"; } catch { }
            }
            else if (Downloads.Any(d => d.Status == DownloadStatus.Failed))
            {
                TaskbarProgress.Update(_taskbarHwnd, 1000, 1000, TaskbarProgress.TaskbarState.Error);
                try { Title = _baseTitle; } catch { }
            }
            else
            {
                TaskbarProgress.Reset(_taskbarHwnd);
                try { Title = _baseTitle; } catch { }
            }
        }
        catch { }
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
        _downloadQueueTimer?.Stop();
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
                        if (item.ScheduledAt.HasValue)
                        {
                            item.WaitForQueueStart = true;
                            item.ScheduledAt = null;
                        }

                        if (item.Status == DownloadStatus.Downloading)
                        {
                            if (SettingsHelper.AutoResumeInterrupted && !item.WaitForQueueStart)
                            {
                                item.ResetCancellationToken();
                                item.Status = DownloadStatus.Queued;
                                item.StatusText = AppLocalization.Get("download.queued");
                                _waitingDownloads.Add(item);
                            }
                            else
                            {
                                item.Status = DownloadStatus.Paused;
                                item.StatusText = AppLocalization.Get("download.interrupted");
                            }
                        }
                        else if (item.Status == DownloadStatus.Queued)
                        {
                            if (item.WaitForQueueStart)
                            {
                                item.StatusText = AppLocalization.Get("queue.savedForLater");
                                _waitingDownloads.Add(item);
                            }
                            else if (SettingsHelper.AutoResumeInterrupted)
                            {
                                item.ResetCancellationToken();
                                item.Status = DownloadStatus.Queued;
                                item.StatusText = AppLocalization.Get("download.queued");
                                _waitingDownloads.Add(item);
                            }
                            else
                            {
                                item.Status = DownloadStatus.Paused;
                                item.StatusText = AppLocalization.Get("download.interrupted");
                            }
                        }
                        Downloads.Add(item);
                    }
                }
                PumpDownloadQueue();
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
                        StatusText = AppLocalization.Get("download.completed"),
                        Progress = 100,
                        SizeText = DownloadEngine.FormatBytes(fi.Length),
                        SpeedText = AppLocalization.Get("download.finished"),
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

    private void QueueDownload(DownloadItem item)
    {
        item.SetDispatcherQueue(DispatcherQueue);
        if (!_waitingDownloads.Any(waiting => waiting.Id == item.Id))
        {
            _waitingDownloads.Add(item);
        }

        item.Status = DownloadStatus.Queued;
        item.StatusText = item.WaitForQueueStart
            ? AppLocalization.Get("queue.savedForLater")
            : AppLocalization.Get("download.queued");
        SaveHistory();
        UpdateQueueButton();
        PumpDownloadQueue();
    }

    private void ShowDownloadPrompt(DownloadItem item)
    {
        var prompt = new DownloadPromptWindow(item, confirmedItem =>
        {
            Downloads.Insert(0, confirmedItem);
            QueueDownload(confirmedItem);
        });
        prompt.ShowAndFocus();
    }

    private void RemoveFromDownloadQueue(DownloadItem item)
    {
        _waitingDownloads.RemoveAll(waiting => waiting.Id == item.Id);
        UpdateQueueButton();
    }

    private void PumpDownloadQueue()
    {
        if (_downloadQueuePaused) return;

        int maximumActive = SettingsHelper.MaximumConcurrentDownloads;
        while (_activeDownloads.Count < maximumActive)
        {
            int nextIndex = _waitingDownloads.FindIndex(item =>
                Downloads.Contains(item) &&
                string.Equals(item.QueueName, "Main queue", StringComparison.OrdinalIgnoreCase) &&
                !item.WaitForQueueStart &&
                item.Status == DownloadStatus.Queued &&
                !_activeDownloads.Contains(item.Id) &&
                !item.Cts.IsCancellationRequested);
            if (nextIndex < 0) return;

            DownloadItem item = _waitingDownloads[nextIndex];
            _waitingDownloads.RemoveAt(nextIndex);
            if (!_activeDownloads.Add(item.Id)) continue;

            item.Status = DownloadStatus.Downloading;
            item.StatusText = AppLocalization.Get("download.starting");
            _ = RunQueuedDownloadAsync(item, item.Cts.Token);
        }
    }

    private async Task RunQueuedDownloadAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(async () => await DownloadEngine.StartDownloadAsync(item, cancellationToken));
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.StatusText = AppLocalization.Format("download.error", ex.Message);
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_waitingDownloads.Any(waiting => waiting.Id == item.Id) && item.Status == DownloadStatus.Paused)
                {
                    item.Status = DownloadStatus.Queued;
                    item.StatusText = item.WaitForQueueStart
                        ? AppLocalization.Get("queue.savedForLater")
                        : AppLocalization.Get("download.queued");
                }
                _activeDownloads.Remove(item.Id);

                // Auto-retry failed downloads (IDM/JDownloader-style).
                if (item.Status == DownloadStatus.Failed && !item.Cts.IsCancellationRequested)
                {
                    int attempts = _retryCounts.TryGetValue(item.Id, out int count) ? count : 0;
                    if (attempts < SettingsHelper.AutoRetryCount)
                    {
                        _retryCounts[item.Id] = attempts + 1;
                        item.ResetCancellationToken();
                        item.Status = DownloadStatus.Queued;
                        item.SpeedText = AppLocalization.Get("download.resuming");
                        item.StatusText = AppLocalization.Get("download.connecting");
                        if (!_waitingDownloads.Any(waiting => waiting.Id == item.Id))
                            _waitingDownloads.Add(item);
                        SaveHistory();
                        UpdateQueueButton();
                        PumpDownloadQueue();
                        return;
                    }
                    _retryCounts.Remove(item.Id);
                }
                else
                {
                    _retryCounts.Remove(item.Id);
                }

                // Completion actions.
                if (item.Status == DownloadStatus.Completed)
                {
                    if (SettingsHelper.PlaySoundOnComplete)
                        PlayCompletionSound();
                    if (SettingsHelper.ShowCompleteDialog)
                        ShowDownloadCompleteDialog(item);
                }

                SaveHistory();
                UpdateQueueButton();
                PumpDownloadQueue();
            });
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
                ShowDownloadPrompt(item);
            }
            else
            {
                // Download immediately without confirmation window
                Downloads.Insert(0, item);
                StatusTextBlock.Text = AppLocalization.Format("main.started", item.Title);
                QueueDownload(item);
            }
        });
    }

    private void OnQueueToggleClick(object sender, RoutedEventArgs e)
    {
        bool hasSavedItems = _waitingDownloads.Any(item =>
            item.Status == DownloadStatus.Queued && item.WaitForQueueStart &&
            string.Equals(item.QueueName, "Main queue", StringComparison.OrdinalIgnoreCase));

        if (_downloadQueuePaused || hasSavedItems)
        {
            _downloadQueuePaused = false;
            foreach (var item in _waitingDownloads.Where(download =>
                         string.Equals(download.QueueName, "Main queue", StringComparison.OrdinalIgnoreCase)))
            {
                if (item.WaitForQueueStart)
                    item.WaitForQueueStart = false;
                if (item.Cts.IsCancellationRequested)
                    item.ResetCancellationToken();
            }

        }
        else
        {
            _downloadQueuePaused = true;
            foreach (var item in Downloads.Where(download => _activeDownloads.Contains(download.Id)).ToList())
            {
                if (!_waitingDownloads.Any(waiting => waiting.Id == item.Id))
                    _waitingDownloads.Add(item);
                item.Cancel();
                item.Status = DownloadStatus.Paused;
                item.SpeedText = AppLocalization.Get("download.pausedSpeed");
                item.StatusText = AppLocalization.Get("download.paused");
            }
        }

        ApplyLocalization();
        StatusTextBlock.Text = AppLocalization.Get(_downloadQueuePaused ? "queue.pausedStatus" : "queue.startedStatus");
        if (!_downloadQueuePaused)
            PumpDownloadQueue();
    }

    private void UpdateQueueButton()
    {
        if (QueueToggleButton == null) return;
        bool hasSavedItems = _waitingDownloads.Any(item =>
            item.Status == DownloadStatus.Queued && item.WaitForQueueStart &&
            string.Equals(item.QueueName, "Main queue", StringComparison.OrdinalIgnoreCase));
        QueueToggleButton.Content = AppLocalization.Get(_downloadQueuePaused || hasSavedItems ? "queue.start" : "queue.pause");
    }

    private void OnAddDownloadClick(object sender, RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusTextBlock.Text = AppLocalization.Get("main.invalidUrl");
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
            StatusText = AppLocalization.Get("download.connecting")
        };

        UrlTextBox.Text = string.Empty;
        if (DownloadPromptWindow.HasYouTubePlaylist(url))
        {
            ShowDownloadPrompt(item);
            StatusTextBlock.Text = AppLocalization.Get("main.playlistOptions");
            return;
        }
        Downloads.Insert(0, item);
        StatusTextBlock.Text = AppLocalization.Format("main.started", item.Title);
        QueueDownload(item);
    }

    private async void OnRemoveDownloadClick(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetItems(sender);
        if (targets.Count == 0)
        {
            StatusTextBlock.Text = AppLocalization.Get("main.nothingSelectedRemove");
            return;
        }
        if (SettingsHelper.ConfirmOnRemove &&
            !await ConfirmAsync(AppLocalization.Format("dialog.removeMessage", targets.Count), AppLocalization.Get("common.remove")))
            return;
        foreach (var item in targets)
        {
            RemoveFromDownloadQueue(item);
            try { item.Cts.Cancel(); } catch { }
            Downloads.Remove(item);
        }
        SaveHistory(immediate: true);
        StatusTextBlock.Text = AppLocalization.Format("main.removedItems", targets.Count);
    }

    private async void OnRemoveSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = DownloadsListView.SelectedItems.OfType<DownloadItem>().ToList();
        if (selected.Count == 0)
        {
            StatusTextBlock.Text = AppLocalization.Get("main.nothingSelectedHelp");
            return;
        }
        if (SettingsHelper.ConfirmOnRemove &&
            !await ConfirmAsync(AppLocalization.Format("dialog.removeMessage", selected.Count), AppLocalization.Get("common.remove")))
            return;
        foreach (var item in selected)
        {
            RemoveFromDownloadQueue(item);
            try { item.Cts.Cancel(); } catch { }
            Downloads.Remove(item);
        }
        SaveHistory(immediate: true);
        StatusTextBlock.Text = AppLocalization.Format("main.removedItems", selected.Count);
    }

    private void OnRemoveCompletedClick(object sender, RoutedEventArgs e)
    {
        var done = Downloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
        if (done.Count == 0)
        {
            StatusTextBlock.Text = AppLocalization.Get("main.noCompleted");
            return;
        }
        foreach (var item in done) Downloads.Remove(item);
        SaveHistory(immediate: true);
        StatusTextBlock.Text = AppLocalization.Format("main.removedCompleted", done.Count);
    }

    private async void OnRemoveAllClick(object sender, RoutedEventArgs e)
    {
        if (Downloads.Count == 0)
        {
            StatusTextBlock.Text = AppLocalization.Get("main.listEmpty");
            return;
        }
        if (SettingsHelper.ConfirmOnRemove &&
            !await ConfirmAsync(AppLocalization.Format("dialog.removeMessage", Downloads.Count), AppLocalization.Get("common.remove")))
            return;
        _waitingDownloads.Clear();
        foreach (var item in Downloads) { try { item.Cts.Cancel(); } catch { } }
        int n = Downloads.Count;
        Downloads.Clear();
        SaveHistory(immediate: true);
        StatusTextBlock.Text = AppLocalization.Format("main.cleared", n);
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // Single-instance real window: focus if already open.
        if (_settingsWindow != null)
        {
            try
            {
                _settingsWindow.RefreshFromSettings();
                _settingsWindow.ShowCentered(this);
            }
            catch { }
            return;
        }

        string previousLanguage = AppLocalization.CurrentLanguage;
        var win = new SettingsWindow();
        _settingsWindow = win;
        win.Closed += (_, _) =>
        {
            bool wasSaved = win.WasSaved;
            _settingsWindow = null;
            AppLocalization.Reload();
            ApplyLocalization();
            ThemeHelper.ApplyTheme(this);
            UpdateClipboardMonitoring();
            PumpDownloadQueue();
            if (StatusTextBlock != null)
            {
                StatusTextBlock.Text = previousLanguage == AppLocalization.CurrentLanguage || !wasSaved
                    ? AppLocalization.Format("main.settingsSaved", SettingsHelper.DownloadFolder)
                    : AppLocalization.Get("main.languageChanged");
            }
        };
        win.ShowCentered(this);
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    /// <summary>App logo from disk (absolute path beats ms-appx flakiness in unpackaged builds).</summary>
    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? LoadLogoImage(int decodeWidth = 0)
    {
        try
        {
            string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo.png");
            if (!File.Exists(logoPath))
                return null;
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logoPath));
            if (decodeWidth > 0)
                image.DecodePixelWidth = decodeWidth;
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void LoadAppLogo()
    {
        try
        {
            var logo = LoadLogoImage(48);
            if (logo != null)
                AppLogoImage.Source = logo;
            else
                AppLogoImage.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private static void PlayCompletionSound()
    {
        try { MessageBeep(0x00000040); } catch { }
    }

    /// <summary>JDownloader-style affirmation dialog. Returns true when confirmed.</summary>
    private async Task<bool> ConfirmAsync(string message, string confirmText)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = AppLocalization.Get("dialog.confirmTitle"),
                Content = message,
                PrimaryButtonText = confirmText,
                CloseButtonText = AppLocalization.Get("settings.cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>IDM-style completion dialog with open options.</summary>
    private async void ShowDownloadCompleteDialog(DownloadItem item)
    {
        try
        {
            string fileName = !string.IsNullOrEmpty(item.SavePath)
                ? Path.GetFileName(item.SavePath)
                : item.Title;
            var dialog = new ContentDialog
            {
                Title = AppLocalization.Get("complete.title"),
                Content = AppLocalization.Format("complete.message", fileName),
                PrimaryButtonText = AppLocalization.Get("complete.openFile"),
                SecondaryButtonText = AppLocalization.Get("complete.openFolder"),
                CloseButtonText = AppLocalization.Get("common.close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.SavePath))
                    Process.Start(new ProcessStartInfo { FileName = item.SavePath, UseShellExecute = true });
                else
                    StatusTextBlock.Text = AppLocalization.Get("main.fileUnavailable");
            }
            else if (result == ContentDialogResult.Secondary)
            {
                if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.SavePath))
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{item.SavePath}\"",
                        UseShellExecute = true
                    });
                else
                    OnOpenFolderClick(this, new RoutedEventArgs());
            }
        }
        catch { }
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var aboutPanel = new StackPanel { Spacing = 12, MinWidth = 360 };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var aboutLogo = LoadLogoImage(64);
        if (aboutLogo != null)
            headerRow.Children.Add(new Image { Source = aboutLogo, Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center });
        
        var titleStack = new StackPanel { Spacing = 2 };
        titleStack.Children.Add(new TextBlock { Text = AppLocalization.Get("app.title"), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titleStack.Children.Add(new TextBlock { Text = AppLocalization.Format("main.version", "26.2.1"), FontSize = 13, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        headerRow.Children.Add(titleStack);
        aboutPanel.Children.Add(headerRow);

        aboutPanel.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("main.aboutDescription"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var detailsStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        detailsStack.Children.Add(new TextBlock { Text = AppLocalization.Get("main.developedBy"), FontSize = 12 });
        detailsStack.Children.Add(new TextBlock { Text = AppLocalization.Get("main.sourceLink"), FontSize = 12 });
        detailsStack.Children.Add(new TextBlock { Text = AppLocalization.Get("main.features"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        aboutPanel.Children.Add(detailsStack);

        var dialog = new ContentDialog
        {
            Title = AppLocalization.Get("main.aboutTitle"),
            Content = aboutPanel,
            CloseButtonText = AppLocalization.Get("settings.cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void UpdateCounts()
    {
        int selected = DownloadsListView?.SelectedItems?.OfType<DownloadItem>().Count() ?? 0;
        DownloadCountTextBlock.Text = AppLocalization.Format("main.count", Downloads.Count, selected);
        if (RemoveSelectedButton != null) RemoveSelectedButton.IsEnabled = selected > 0;
        UpdateQueueButton();
    }

    private void ApplyLocalization()
    {
        _baseTitle = $"{AppLocalization.Get("app.title")} 26.2.1";
        Title = _baseTitle;
        if (Content is FrameworkElement root)
            root.FlowDirection = AppLocalization.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        AppNameTextBlock.Text = AppLocalization.Get("app.title");
        UrlTextBox.PlaceholderText = AppLocalization.Get("main.urlPlaceholder");
        AddDownloadButton.Content = AppLocalization.Get("main.add");
        OpenFolderButton.Content = AppLocalization.Get("main.openFolder");
        RemoveSelectedButton.Content = AppLocalization.Get("main.removeSelected");
        RemoveCompletedButton.Content = AppLocalization.Get("main.removeCompleted");
        ToolTipService.SetToolTip(RemoveCompletedButton, AppLocalization.Get("main.removeCompletedTooltip"));
        RemoveAllButton.Content = AppLocalization.Get("main.removeAll");
        ToolTipService.SetToolTip(RemoveAllButton, AppLocalization.Get("main.removeAllTooltip"));
        CompanionLabelTextBlock.Text = AppLocalization.Get("main.companion");
        UpdateQueueButton();
        BridgeStatusTextBlock.Text = $"● {AppLocalization.Get("main.listening")}";
        ToolTipService.SetToolTip(SettingsButton, AppLocalization.Get("main.settings"));
        ToolTipService.SetToolTip(AboutButton, AppLocalization.Get("main.about"));
        foreach (var item in Downloads)
        {
            if (DownloadsListView.ContainerFromItem(item) is ListViewItem container)
                SetDownloadContextFlyout(container, item);
        }
        if (StatusTextBlock != null) StatusTextBlock.Text = AppLocalization.Get("main.ready");
        UpdateCounts();
    }

    private void OnDownloadsContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is not ListViewItem container || args.Item is not DownloadItem item)
            return;

        SetDownloadContextFlyout(container, item);
    }

    private void SetDownloadContextFlyout(ListViewItem container, DownloadItem item)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(CreateContextMenuItem("menu.pauseResume", item, OnTogglePauseDownloadClick, "\uE768"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateContextMenuItem("menu.removeList", item, OnRemoveDownloadClick, "\uE74D"));
        menu.Items.Add(CreateContextMenuItem("menu.deleteFile", item, OnDeleteDownloadedFileClick, "\uE74D"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateContextMenuItem("menu.openFile", item, OnOpenFileClick, "\uE8E5"));
        menu.Items.Add(CreateContextMenuItem("menu.openContainingFolder", item, OnOpenContainingFolderClick, "\uE8B7"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateContextMenuItem("menu.copyLink", item, OnCopyDownloadLinkClick, "\uE8C8"));
        container.ContextFlyout = menu;
    }

    private MenuFlyoutItem CreateContextMenuItem(string key, DownloadItem item, RoutedEventHandler handler, string glyph)
    {
        var menuItem = new MenuFlyoutItem
        {
            Text = AppLocalization.Get(key),
            DataContext = item,
            Icon = new FontIcon { Glyph = glyph }
        };
        menuItem.Click += handler;
        return menuItem;
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
                RemoveFromDownloadQueue(item);
                // Pause active download
                item.Cancel();
                item.Status = DownloadStatus.Paused;
                item.SpeedText = AppLocalization.Get("download.pausedSpeed");
                item.StatusText = AppLocalization.Get("download.paused");
                StatusTextBlock.Text = AppLocalization.Format("main.pausedDownload", item.Title);
            }
            else if (item.Status == DownloadStatus.Paused || item.Status == DownloadStatus.Failed)
            {
                // Resume download
                item.ResetCancellationToken();
                item.Status = DownloadStatus.Queued;
                item.SpeedText = AppLocalization.Get("download.resuming");
                item.StatusText = AppLocalization.Get("download.connecting");
                StatusTextBlock.Text = AppLocalization.Format("main.resumedDownload", item.Title);
                QueueDownload(item);
            }
        }
        SaveHistory();
    }

    private async void OnDeleteDownloadedFileClick(object sender, RoutedEventArgs e)
    {
        var targets = GetTargetItems(sender);
        if (targets.Count == 0)
        {
            StatusTextBlock.Text = AppLocalization.Get("main.nothingSelectedDelete");
            return;
        }
        if (SettingsHelper.ConfirmOnDeleteFile &&
            !await ConfirmAsync(AppLocalization.Format("dialog.deleteMessage", targets.Count), AppLocalization.Get("common.delete")))
            return;
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
        StatusTextBlock.Text = AppLocalization.Format("main.deletedItems", targets.Count, deleted);
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
                StatusTextBlock.Text = AppLocalization.Get("main.fileUnavailable");
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
            _lastClipboardUrl = item.Url;
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(item.Url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            StatusTextBlock.Text = AppLocalization.Get("main.copiedLink");
        }
    }

    private void UpdateClipboardMonitoring()
    {
        bool shouldMonitor = SettingsHelper.ClipboardMonitorEnabled;
        if (shouldMonitor == _clipboardMonitoringAttached) return;

        if (shouldMonitor)
            Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += OnClipboardContentChanged;
        else
            Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= OnClipboardContentChanged;

        _clipboardMonitoringAttached = shouldMonitor;
    }

    private async void OnClipboardContentChanged(object? sender, object e)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;

            string candidate = (await content.GetTextAsync()).Trim();
            if (candidate.Length == 0 || candidate == _lastClipboardUrl ||
                !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            _lastClipboardUrl = candidate;
            string title = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(title)) title = uri.Host;
            var item = new DownloadItem
            {
                Url = candidate,
                PageUrl = candidate,
                Referrer = candidate,
                Title = title,
                Quality = SettingsHelper.DefaultQuality,
                Status = DownloadStatus.Queued,
                StatusText = AppLocalization.Get("download.queued")
            };

            DispatcherQueue.TryEnqueue(() => ShowDownloadPrompt(item));
        }
        catch { }
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
