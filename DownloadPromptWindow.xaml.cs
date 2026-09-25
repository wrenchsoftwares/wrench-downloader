using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace WrenchDownloader;

public sealed partial class DownloadPromptWindow : Window
{
    private readonly DownloadItem _item;
    private readonly Action<DownloadItem> _onConfirmed;

    public DownloadPromptWindow(DownloadItem item, Action<DownloadItem> onConfirmed)
    {
        _item = item;
        _onConfirmed = onConfirmed;
        InitializeComponent();

        Title = $"Download - {item.Title}";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(660, 440));

        TitleBox.Text = item.Title;
        UrlBox.Text = item.Url;
        
        string downloads = SettingsHelper.DownloadFolder;
        FolderBox.Text = downloads;
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
            // If item.SavePath is set or to be determined, configure target folder
            string filename = !string.IsNullOrEmpty(_item.SavePath) ? Path.GetFileName(_item.SavePath) : $"{_item.Title}";
            _item.SavePath = Path.Combine(chosenFolder, filename);
        }

        _onConfirmed?.Invoke(_item);
        this.Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
