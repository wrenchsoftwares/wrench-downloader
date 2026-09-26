using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WrenchDownloader;

/// <summary>
/// The main content page displayed inside the application window.
/// Add your UI logic, event handlers, and data binding here.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        PageTitleTextBlock.Text = $"🔧 {AppLocalization.Get("app.title")}";
        SubtitleTextBlock.Text = AppLocalization.Get("main.subtitle");
        UrlTextBox.PlaceholderText = AppLocalization.Get("main.urlPlaceholder");
        AddDownloadButton.Content = AppLocalization.Get("main.add");
        GrabVideoButton.Content = AppLocalization.Get("main.grabVideo");
        StatusTextBlock.Text = AppLocalization.Get("main.ready");
        FlowDirection = AppLocalization.IsRightToLeft ? Microsoft.UI.Xaml.FlowDirection.RightToLeft : Microsoft.UI.Xaml.FlowDirection.LeftToRight;
    }

    private void OnAddDownloadClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            StatusTextBlock.Text = AppLocalization.Format("main.startedTask", url);
        }
        else
        {
            StatusTextBlock.Text = AppLocalization.Get("main.invalidUrl");
        }
    }

    private void OnGrabVideoClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        StatusTextBlock.Text = AppLocalization.Get("main.sniffing");
    }
}
