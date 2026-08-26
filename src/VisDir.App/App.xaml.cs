using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;

namespace VisDir.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        SafeLog($"[App] OnStartup at {DateTime.Now}");
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            SafeLog($"[AppDomain Crash] {args.ExceptionObject}");
        };
        DispatcherUnhandledException += (s, args) =>
        {
            SafeLog($"[Dispatcher Crash] {args.Exception}");
        };
        base.OnStartup(e);
    }

    private static void SafeLog(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisDir");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"), $"{message}\n");
        }
        catch
        {
            // Logging failure should never crash the application
        }
    }
}

