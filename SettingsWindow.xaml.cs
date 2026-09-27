using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WrenchDownloader;

/// <summary>
/// Settings as a real resizable window (drag via title bar, resize via borders).
/// Tabs: Downloads / Behavior / Appearance (display mode + language).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly string[] Qualities =
        ["best", "2160p", "1440p", "1080p", "720p", "480p", "360p"];

    private bool _suppressThemePreview;

    /// <summary>True when the user clicked Save (vs Cancel / X).</summary>
    public bool WasSaved { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        SetupWindowChrome();
        LoadQualities();
        LoadThemes();
        LoadLanguages();
        RefreshFromSettings();
        ApplyLocalization();
        ThemeHelper.ApplyTheme(this);
        // If closed without saving, revert any live theme preview.
        Closed += (_, _) =>
        {
            if (!WasSaved)
                ThemeHelper.ApplyTheme(App.MainWindowInstance);
        };
    }

    private void SetupWindowChrome()
    {
        try
        {
            // Default is Overlapped (draggable + resizable). Ensure flags are on.
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }

            // Sensible default + minimum size.
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display?.WorkArea ?? new RectInt32 { Width = 1920, Height = 1080 };
            int width = Math.Min(900, work.Width - 80);
            int height = Math.Min(720, work.Height - 80);
            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.SetIcon("Assets\\AppIcon.ico");
        }
        catch { }
    }

    /// <summary>Center over the owner (main window) and show.</summary>
    public void ShowCentered(Window? owner = null)
    {
        try
        {
            Window? parent = owner ?? App.MainWindowInstance;
            if (parent != null)
            {
                var parentArea = DisplayArea.GetFromWindowId(parent.AppWindow.Id, DisplayAreaFallback.Primary);
                var thisArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                var area = parentArea ?? thisArea;
                if (area != null)
                {
                    var work = area.WorkArea;
                    var size = AppWindow.Size;
                    AppWindow.Move(new PointInt32(
                        work.X + Math.Max(0, (work.Width - size.Width) / 2),
                        work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
                }
            }
        }
        catch { }
        Activate();
    }

    private void LoadQualities()
    {
        QualityBox.ItemsSource = new List<string>(Qualities);
    }

    private void LoadThemes()
    {
        ThemeBox.Items.Clear();
        ThemeBox.Items.Add(new ComboBoxItem { Content = AppLocalization.Get("theme.system"), Tag = "system" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = AppLocalization.Get("theme.light"), Tag = "light" });
        ThemeBox.Items.Add(new ComboBoxItem { Content = AppLocalization.Get("theme.dark"), Tag = "dark" });
    }

    private void LoadLanguages()
    {
        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(new ComboBoxItem { Content = AppLocalization.Get("language.english"), Tag = "en" });
        LanguageBox.Items.Add(new ComboBoxItem { Content = AppLocalization.Get("language.french"), Tag = "fr" });
        LanguageBox.Items.Add(new ComboBoxItem { Content = AppLocalization.Get("language.arabic"), Tag = "ar" });
    }

    public void RefreshFromSettings()
    {
        AppLocalization.Reload();
        _suppressThemePreview = true;
        try
        {
            FolderBox.Text = SettingsHelper.DownloadFolder;
            FragmentsBox.Value = SettingsHelper.ConcurrentFragments;
            MaxDownloadsBox.Value = SettingsHelper.MaximumConcurrentDownloads;
            MaxRateBox.Value = SettingsHelper.MaximumDownloadRateKBps;
            QualityBox.SelectedItem = SettingsHelper.DefaultQuality;
            if (QualityBox.SelectedItem == null)
                QualityBox.SelectedIndex = 0;
            RetryBox.Value = SettingsHelper.AutoRetryCount;
            ShowDialogCheck.IsChecked = SettingsHelper.ShowDownloadDialog;
            ClipboardCheck.IsChecked = SettingsHelper.ClipboardMonitorEnabled;
            OrganizeCheck.IsChecked = SettingsHelper.OrganizeDownloadsByType;

            ShowCompleteDialogCheck.IsChecked = SettingsHelper.ShowCompleteDialog;
            PlaySoundCheck.IsChecked = SettingsHelper.PlaySoundOnComplete;
            ConfirmDeleteCheck.IsChecked = SettingsHelper.ConfirmOnDeleteFile;
            AutoResumeCheck.IsChecked = SettingsHelper.AutoResumeInterrupted;

            ThemeBox.SelectedIndex = SettingsHelper.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
            LanguageBox.SelectedIndex = SettingsHelper.Language switch { "fr" => 1, "ar" => 2, _ => 0 };

#if !DEBUG
            CloseToTrayCheck.IsChecked = SettingsHelper.CloseToTray;
            StartWithWindowsCheck.IsChecked = SettingsHelper.StartWithWindows;
            StartWithWindowsCheck.Visibility = SettingsHelper.IsPortable ? Visibility.Collapsed : Visibility.Visible;
            WindowBehaviorHeader.Visibility = Visibility.Visible;
            CloseToTrayCheck.Visibility = Visibility.Visible;
            PortableInfoText.Visibility = Visibility.Collapsed;
#else
            WindowBehaviorHeader.Visibility = Visibility.Collapsed;
            CloseToTrayCheck.Visibility = Visibility.Collapsed;
            StartWithWindowsCheck.Visibility = Visibility.Collapsed;
            PortableInfoText.Visibility = Visibility.Visible;
#endif
        }
        finally
        {
            _suppressThemePreview = false;
        }
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        Title = AppLocalization.Get("settings.title");
        if (Content is FrameworkElement root)
            root.FlowDirection = AppLocalization.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        SaveButton.Content = AppLocalization.Get("settings.save");
        CancelButton.Content = AppLocalization.Get("settings.cancel");
        BrowseButton.Content = AppLocalization.Get("prompt.browse");
        DownloadLocationHeader.Text = AppLocalization.Get("settings.downloadLocation");
        FragmentsBox.Header = AppLocalization.Get("settings.parallelFragments");
        MaxDownloadsBox.Header = AppLocalization.Get("settings.maximumDownloads");
        MaxRateBox.Header = AppLocalization.Get("settings.maximumRate");
        QualityBox.Header = AppLocalization.Get("settings.defaultQuality");
        RetryBox.Header = AppLocalization.Get("settings.autoRetry");
        ShowDialogCheck.Content = AppLocalization.Get("settings.confirmation");
        ClipboardCheck.Content = AppLocalization.Get("settings.clipboardMonitor");
        OrganizeCheck.Content = AppLocalization.Get("settings.organizeDownloads");

        DownloadsTab.Header = AppLocalization.Get("settings.downloadsTab");
        BehaviorTab.Header = AppLocalization.Get("settings.behaviorTab");
        BrowsersTab.Header = AppLocalization.Get("settings.browsersTab");
        BrowserHeader.Text = AppLocalization.Get("settings.browserHeader");
        BrowserHint.Text = AppLocalization.Get("settings.browserHint");
        Step1Prefix.Text = AppLocalization.Get("settings.browserStep1Before");
        Step1Suffix.Text = AppLocalization.Get("settings.browserStep1After");
        Step2Text.Text = AppLocalization.Get("settings.browserStep2");
        Step3Text.Text = AppLocalization.Get("settings.browserStep3");
        Step4Text.Text = AppLocalization.Get("settings.browserStep4");
        ChromeShowFolderButton.Content = AppLocalization.Get("ext.showFolder");
        FirefoxButton.Content = AppLocalization.Get("ext.comingSoon");
        AppearanceTab.Header = AppLocalization.Get("settings.appearanceTab");

        ThemeHeader.Text = AppLocalization.Get("settings.themeHeader");
        ThemeHint.Text = AppLocalization.Get("settings.themeHint");
        LanguageHeader.Text = AppLocalization.Get("settings.languageHeader");
        LanguageHint.Text = AppLocalization.Get("settings.languageHint");

        if (ThemeBox.Items.Count >= 3)
        {
            if (ThemeBox.Items[0] is ComboBoxItem sys) sys.Content = AppLocalization.Get("theme.system");
            if (ThemeBox.Items[1] is ComboBoxItem light) light.Content = AppLocalization.Get("theme.light");
            if (ThemeBox.Items[2] is ComboBoxItem dark) dark.Content = AppLocalization.Get("theme.dark");
        }
        if (LanguageBox.Items.Count >= 3)
        {
            if (LanguageBox.Items[0] is ComboBoxItem en) en.Content = AppLocalization.Get("language.english");
            if (LanguageBox.Items[1] is ComboBoxItem fr) fr.Content = AppLocalization.Get("language.french");
            if (LanguageBox.Items[2] is ComboBoxItem ar) ar.Content = AppLocalization.Get("language.arabic");
        }

        CompletionHeader.Text = AppLocalization.Get("settings.completionHeader");
        ShowCompleteDialogCheck.Content = AppLocalization.Get("settings.showCompleteDialog");
        PlaySoundCheck.Content = AppLocalization.Get("settings.playSound");
        ConfirmationsHeader.Text = AppLocalization.Get("settings.confirmationsHeader");
        ConfirmDeleteCheck.Content = AppLocalization.Get("settings.confirmDeleteFile");
        StartupHeader.Text = AppLocalization.Get("settings.startupHeader");
        AutoResumeCheck.Content = AppLocalization.Get("settings.autoResume");

#if !DEBUG
        WindowBehaviorHeader.Text = AppLocalization.Get("settings.windowBehavior");
        CloseToTrayCheck.Content = AppLocalization.Get("settings.closeToTray");
        StartWithWindowsCheck.Content = AppLocalization.Get("settings.startWithWindows");
#else
        PortableInfoText.Text = AppLocalization.Get("settings.portableInfo");
#endif
    }

    private string SelectedTheme() =>
        (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Live preview: apply immediately to all open windows, persist on Save.
        if (_suppressThemePreview || ThemeBox.SelectedItem == null) return;
        try
        {
            ElementTheme preview = ThemeHelper.ToElementTheme(SelectedTheme());
            if (Content is FrameworkElement root)
                root.RequestedTheme = preview;
            if (App.MainWindowInstance?.Content is FrameworkElement mainRoot)
                mainRoot.RequestedTheme = preview;
        }
        catch { }
    }

    private void OnShowExtensionFolderClick(object sender, RoutedEventArgs e)
    {
        if (ChromeExtensionInstaller.OpenStagedFolder(out string stagedPath, out string? error))
            BrowserStatusText.Text = AppLocalization.Format("main.extensionStaged", stagedPath);
        else
            BrowserStatusText.Text = AppLocalization.Format("main.extensionStageFailed", error ?? "?");
    }

    private void OnCopyExtensionsAddressClick(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        // chrome:// URLs can't be launched reliably from an unpackaged app,
        // so copy the address for the user to paste into Chrome instead.
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText("chrome://extensions");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            BrowserStatusText.Text = AppLocalization.Get("ext.copiedAddress");
        }
        catch { }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
                FolderBox.Text = folder.Path;
        }
        catch { }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string quality = QualityBox.SelectedItem as string ?? "best";
        bool showDialog = ShowDialogCheck.IsChecked ?? true;
        int maximumDownloads = (int)Math.Round(MaxDownloadsBox.Value);
        int fragments = (int)Math.Round(FragmentsBox.Value);
        int maxRate = (int)Math.Round(MaxRateBox.Value);
        int autoRetry = (int)Math.Round(RetryBox.Value);
        bool clipboard = ClipboardCheck.IsChecked == true;
        bool organize = OrganizeCheck.IsChecked == true;
        bool showComplete = ShowCompleteDialogCheck.IsChecked == true;
        bool playSound = PlaySoundCheck.IsChecked == true;
        bool confirmDelete = ConfirmDeleteCheck.IsChecked == true;
        bool autoResume = AutoResumeCheck.IsChecked == true;

#if !DEBUG
        bool closeToTray = CloseToTrayCheck.IsChecked ?? true;
        bool startWithWindows = StartWithWindowsCheck.IsChecked ?? false;
#else
        bool closeToTray = SettingsHelper.CloseToTray;
        bool startWithWindows = SettingsHelper.StartWithWindows;
#endif
        SettingsHelper.Save(FolderBox.Text, fragments, quality, showDialog,
            closeToTray, startWithWindows, maximumDownloads, maxRate,
            clipboard, organize, showComplete, playSound,
            confirmDelete, autoResume, autoRetry);
        SettingsHelper.SaveTheme(SelectedTheme());
        SettingsHelper.SaveLanguage((LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "en");
        WasSaved = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        WasSaved = false;
        Close();
    }
}
