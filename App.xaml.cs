using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WrenchDownloader;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    internal static Window? MainWindowInstance { get; private set; }
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        string logPath = PortablePaths.StartupLogPath;
        System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] App() constructor start\n");
        this.UnhandledException += (s, e) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] APP UNHANDLED EXCEPTION: {e.Message} | {e.Exception}\n");
            e.Handled = true;
        };
        System.AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] DOMAIN UNHANDLED EXCEPTION: {e.ExceptionObject}\n");
        };
        InitializeComponent();
        System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] App() constructor completed\n");
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        string logPath = PortablePaths.StartupLogPath;
        System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] OnLaunched start\n");
        try
        {
            _window = new MainWindow();
            MainWindowInstance = _window;
            System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] MainWindow instantiated\n");

            bool startBackground = false;
#if !DEBUG
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "--background", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "/background", System.StringComparison.OrdinalIgnoreCase))
                {
                    startBackground = true;
                    break;
                }
            }
#endif

            if (!startBackground)
            {
                _window.Activate();
                System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] MainWindow.Activate called\n");
            }
            else
            {
                System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] Started in background tray mode\n");
            }
        }
        catch (System.Exception ex)
        {
            System.IO.File.AppendAllText(logPath, $"[{System.DateTime.Now}] ERROR in OnLaunched: {ex}\n");
            throw;
        }
    }
}
