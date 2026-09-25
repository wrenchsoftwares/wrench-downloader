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

        // TODO: Add your initialization logic here.
    }

    private void OnAddDownloadClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            StatusTextBlock.Text = $"Started download task for: {url}";
        }
        else
        {
            StatusTextBlock.Text = "Please enter a valid URL.";
        }
    }

    private void OnGrabVideoClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Sniffing video streams from browser extension...";
    }
}
