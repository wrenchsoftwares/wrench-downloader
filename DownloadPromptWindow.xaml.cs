using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace WrenchDownloader;

public sealed partial class DownloadPromptWindow : Window
{
    private readonly DownloadItem _item;
    private readonly Action<DownloadItem> _onConfirmed;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    public DownloadPromptWindow(DownloadItem item, Action<DownloadItem> onConfirmed)
    {
        _item = item;
        _onConfirmed = onConfirmed;
        InitializeComponent();
        ThemeHelper.ApplyTheme(this);

        ApplyLocalization();
        Title = AppLocalization.Format("prompt.windowTitle", item.Title);

        TitleBox.Text = item.Title;
        UrlBox.Text = item.Url;
        PlaylistCheckBox.Visibility = HasYouTubePlaylist(item.Url) || HasYouTubePlaylist(item.PageUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlaylistCheckBox.IsChecked = item.DownloadPlaylist;

        DownloadLaterCheckBox.IsChecked = item.WaitForQueueStart || item.ScheduledAt.HasValue;
        QueueSelector.Visibility = DownloadLaterCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        QueueSelector.Items.Add(new Microsoft.UI.Xaml.Controls.ComboBoxItem
        {
            Content = AppLocalization.Get("queue.main"),
            Tag = "Main queue"
        });
        QueueSelector.SelectedIndex = 0;
        QueueSelector.IsEnabled = DownloadLaterCheckBox.IsChecked == true;
        StartButton.Content = AppLocalization.Get(DownloadLaterCheckBox.IsChecked == true ? "prompt.saveForLater" : "prompt.startButton");
        
        string downloads = SettingsHelper.DownloadFolder;
        FolderBox.Text = downloads;
    }

    private void ApplyLocalization()
    {
        if (Content is FrameworkElement root)
            root.FlowDirection = AppLocalization.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Title = AppLocalization.Get("prompt.title");
        PromptHeadingTextBlock.Text = AppLocalization.Get("prompt.start");
        TitleBox.Header = AppLocalization.Get("prompt.fileName");
        TitleBox.PlaceholderText = AppLocalization.Get("prompt.fileName");
        UrlBox.Header = AppLocalization.Get("prompt.url");
        PlaylistCheckBox.Content = AppLocalization.Get("prompt.playlist");
        FolderBox.Header = AppLocalization.Get("prompt.saveFolder");
        BrowseButton.Content = AppLocalization.Get("prompt.browse");
        DownloadLaterCheckBox.Content = AppLocalization.Get("prompt.downloadLater");
        if (QueueSelector.Items.Count > 0 && QueueSelector.Items[0] is Microsoft.UI.Xaml.Controls.ComboBoxItem queueItem)
            queueItem.Content = AppLocalization.Get("queue.main");
        CancelButton.Content = AppLocalization.Get("prompt.cancel");
        StartButton.Content = AppLocalization.Get("prompt.startButton");
    }

    public void ShowAndFocus()
    {
        ResizeToContent();
        Activate();
        DispatcherQueue.TryEnqueue(ResizeToContent);
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hWnd, SW_RESTORE);
            BringWindowToTop(hWnd);
            // Briefly set TOPMOST and then NOTOPMOST to force window to pop in front of any full screen or browser window
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hWnd);
            SwitchToThisWindow(hWnd, true);
        }
        catch { }
    }

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                FolderBox.Text = folder.Path;
            }
        }
        catch { }
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        string title = TitleBox.Text.Trim();
        if (!string.IsNullOrEmpty(title))
        {
            _item.Title = title;
        }

        string chosenFolder = FolderBox.Text.Trim();
        if (!string.IsNullOrEmpty(chosenFolder) && Directory.Exists(chosenFolder))
        {
            _item.TargetFolder = chosenFolder;
        }

        _item.DownloadPlaylist = PlaylistCheckBox.Visibility == Visibility.Visible && PlaylistCheckBox.IsChecked == true;
        _item.WaitForQueueStart = DownloadLaterCheckBox.IsChecked == true;
        _item.ScheduledAt = null;
        _item.QueueName = (QueueSelector.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string ?? "Main queue";

        _onConfirmed?.Invoke(_item);
        this.Close();
    }

    public static bool HasYouTubePlaylist(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !(uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(System.Web.HttpUtility.ParseQueryString(uri.Query)["list"]);
    }

    private void OnDownloadLaterChanged(object sender, RoutedEventArgs e)
    {
        QueueSelector.Visibility = DownloadLaterCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        QueueSelector.IsEnabled = DownloadLaterCheckBox.IsChecked == true;
        StartButton.Content = AppLocalization.Get(DownloadLaterCheckBox.IsChecked == true ? "prompt.saveForLater" : "prompt.startButton");
        DispatcherQueue.TryEnqueue(ResizeToContent);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void ResizeToContent()
    {
        try
        {
            PromptContentPanel.UpdateLayout();
            PromptContentPanel.Measure(new Windows.Foundation.Size(640, 1200));
            double dipContentHeight = PromptContentPanel.DesiredSize.Height > 0
                ? PromptContentPanel.DesiredSize.Height
                : PromptContentPanel.ActualHeight;

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hWnd);
            double scale = (dpi > 0) ? (dpi / 96.0) : 1.0;

            // Total DIP height: Grid vertical padding (18 top + 18 bottom = 36) + ContentHeight
            double totalDipHeight = dipContentHeight + 36;

            // Physical window dimensions (AppWindow.Resize takes physical pixels in WinUI 3)
            // Non-client frame overhead (title bar ~32-40px + border ~8px = ~48px physical)
            int physicalWidth = (int)Math.Round(660 * scale);
            int physicalHeight = (int)Math.Ceiling((totalDipHeight * scale) + (48 * scale));

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                physicalHeight = Math.Clamp(physicalHeight, (int)(280 * scale), workArea.Height - 40);
                AppWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
                AppWindow.Move(new Windows.Graphics.PointInt32(
                    workArea.X + (workArea.Width - physicalWidth) / 2,
                    workArea.Y + (workArea.Height - physicalHeight) / 2));
            }
            else
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
            }
        }
        catch { }
    }
}
