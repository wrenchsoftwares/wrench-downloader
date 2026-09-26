using Microsoft.UI.Xaml;

namespace WrenchDownloader;

/// <summary>
/// Applies the saved display mode (system / light / dark) to windows.
/// WinUI 3 cannot change Application.RequestedTheme at runtime, so the
/// theme is applied per-window via the root element's RequestedTheme.
/// </summary>
public static class ThemeHelper
{
    public static ElementTheme ToElementTheme(string? theme) => theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void ApplyTheme(Window? window)
    {
        if (window?.Content is FrameworkElement root)
        {
            ElementTheme wanted = ToElementTheme(SettingsHelper.Theme);
            if (root.RequestedTheme != wanted)
                root.RequestedTheme = wanted;
        }
    }
}
