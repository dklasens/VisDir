using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using VisDir.Scanner;

namespace VisDir.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--worker", .. var workerArgs])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = ScannerCli.Execute(workerArgs);
            Shutdown(exitCode);
            return;
        }

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
            string logPath = Path.Combine(dir, "debug.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 1_048_576)
            {
                string previous = logPath + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(logPath, previous);
            }
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging failure should never crash the application
        }
    }
}

