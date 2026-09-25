using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WrenchDownloader;

/// <summary>
/// Settings dialog built in code: download folder, parallelism, default quality.
/// </summary>
public sealed class SettingsDialog : ContentDialog
{
    private readonly TextBox _folderBox;
    private readonly NumberBox _fragmentsBox;
    private readonly ComboBox _qualityBox;
    private readonly CheckBox _showDialogCheckBox;

    private static readonly string[] Qualities =
        ["best", "2160p", "1440p", "1080p", "720p", "480p", "360p"];

    public SettingsDialog()
    {
        Title = "Settings";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _folderBox = new TextBox
        {
            Text = SettingsHelper.DownloadFolder,
            IsReadOnly = true
        };
        var browseButton = new Button { Content = "Browse…" };
        browseButton.Click += OnBrowseClick;
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(_folderBox);
        folderRow.Children.Add(browseButton);
        Grid.SetColumn(_folderBox, 0);
        Grid.SetColumn(browseButton, 1);

        _fragmentsBox = new NumberBox
        {
            Minimum = 1,
            Maximum = 16,
            Value = SettingsHelper.ConcurrentFragments,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 1,
            LargeChange = 2
        };

        _qualityBox = new ComboBox
        {
            ItemsSource = new List<string>(Qualities),
            SelectedItem = SettingsHelper.DefaultQuality
        };
        if (_qualityBox.SelectedItem == null)
            _qualityBox.SelectedIndex = 0;

        _showDialogCheckBox = new CheckBox
        {
            Content = "Show download confirmation dialog before starting download",
            IsChecked = SettingsHelper.ShowDownloadDialog,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Download folder" });
        panel.Children.Add(folderRow);
        panel.Children.Add(new TextBlock { Text = "Parallel fragments per download", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_fragmentsBox);
        panel.Children.Add(new TextBlock { Text = "Default quality for pasted URLs", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_qualityBox);
        panel.Children.Add(_showDialogCheckBox);

        Content = panel;
        PrimaryButtonClick += OnSaveClick;
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            var window = App.MainWindowInstance;
            if (window != null)
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
                _folderBox.Text = folder.Path;
        }
        catch { }
    }

    private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string quality = _qualityBox.SelectedItem as string ?? "best";
        bool showDialog = _showDialogCheckBox.IsChecked ?? true;
        SettingsHelper.Save(_folderBox.Text, (int)Math.Round(_fragmentsBox.Value), quality, showDialog);
    }
}
