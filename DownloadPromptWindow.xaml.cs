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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 360));

        TitleBox.Text = item.Title;
        UrlBox.Text = item.Url;
        
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        FolderBox.Text = downloads;
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _item.Title = TitleBox.Text.Trim();
        _onConfirmed?.Invoke(_item);
        this.Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
