using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace WrenchDownloader;

public sealed partial class TorrentFilesWindow : Window
{
    public sealed class TorrentFileRow : INotifyPropertyChanged
    {
        public string Path { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string SizeText { get; init; } = "";
        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly string _magnetUri;
    private readonly Action<TorrentMetadata, List<string>, string> _onConfirmed;
    private readonly ObservableCollection<TorrentFileRow> _rows = new();
    private TorrentMetadata? _meta;
    private bool _wasConfirmed;

    public string TargetFolder { get; } = SettingsHelper.DownloadFolder;

    /// <summary>Opens immediately (IDM-style) and resolves the file list
    /// inside the dialog, so a click always gets instant feedback.</summary>
    public TorrentFilesWindow(string magnetUri, Action<TorrentMetadata, List<string>, string> onConfirmed)
    {
        _magnetUri = magnetUri;
        _onConfirmed = onConfirmed;
        InitializeComponent();
        ThemeHelper.ApplyTheme(this);
        Title = "Torrent Files";

        TorrentNameTextBlock.Text = "Resolving torrent…";
        TorrentSummaryTextBlock.Text = "";
        ResolvingPanel.Visibility = Visibility.Visible;
        ResolvingRing.IsActive = true;
        StartButton.IsEnabled = false;
        StartButton.Content = "Resolving…";
        FilesList.ItemsSource = _rows;
        Closed += OnWindowClosed;

        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(660, 600));
            AppWindow.SetIcon("Assets\\AppIcon.ico");
        }
        catch { }

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        TorrentMetadata? meta = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            meta = await TorrentEngine.FetchMetadataAsync(_magnetUri, cts.Token);
        }
        catch (Exception ex)
        {
            ResolvingRing.IsActive = false;
            ResolvingPanel.Visibility = Visibility.Collapsed;
            TorrentNameTextBlock.Text = "Could not resolve torrent";
            TorrentSummaryTextBlock.Text = ex.Message;
            StartButton.IsEnabled = false;
            StartButton.Content = "Start Download";
            CancelButton.Content = "Close";
            return;
        }
        _meta = meta;
        TorrentNameTextBlock.Text = meta.Name;
        long total = meta.Files.Sum(f => f.Length);
        TorrentSummaryTextBlock.Text =
            $"{meta.Files.Count} file(s) • {DownloadEngine.FormatBytes(total)} • → {TargetFolder}";
        _rows.Clear();
        foreach (var f in meta.Files.OrderByDescending(f => f.Length))
        {
            _rows.Add(new TorrentFileRow
            {
                Path = f.Path,
                DisplayName = f.Path.Replace('/', Path.DirectorySeparatorChar),
                SizeText = DownloadEngine.FormatBytes(f.Length),
                IsSelected = true
            });
        }
        ResolvingRing.IsActive = false;
        ResolvingPanel.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = true;
        StartButton.Content = "Start Download";
    }

    private void OnWindowClosed(object? sender, WindowEventArgs args)
    {
        if (!_wasConfirmed && _meta != null)
        {
            var m = _meta;
            _ = TorrentEngine.DiscardAsync(m);
        }
    }

    /// <summary>Center over the owner (main window) and show.</summary>
    public void ShowCentered(Window? owner = null)
    {
        try
        {
            Window? parent = owner ?? App.MainWindowInstance;
            if (parent != null)
            {
                var area = DisplayArea.GetFromWindowId(parent.AppWindow.Id, DisplayAreaFallback.Primary)
                    ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                if (area != null)
                {
                    var work = area.WorkArea;
                    var size = AppWindow.Size;
                    AppWindow.Move(new Windows.Graphics.PointInt32(
                        work.X + Math.Max(0, (work.Width - size.Width) / 2),
                        work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
                }
            }
        }
        catch { }
        Activate();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = true;
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = false;
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_meta == null) return;
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Path).ToList();
        if (selected.Count == 0) return;
        _wasConfirmed = true;
        try { _onConfirmed?.Invoke(_meta, selected, TargetFolder); } catch { }
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
