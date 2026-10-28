using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using VisDir.Scanner;
using VisDir.App.Update;

namespace VisDir.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        VisDir.Core.Interop.TokenPrivilegeManager.TryEnableBackupPrivileges();

        if (e.Args is ["--worker-pipe", var pipeName, var parentId])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(int.TryParse(parentId, out int processId) ? WorkerHost.Execute(pipeName, processId) : 1);
            return;
        }

        if (e.Args is ["--worker-pipe", ..])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(1);
            return;
        }

        if (e.Args is ["--worker", .. var workerArgs])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = ScannerCli.Execute(workerArgs);
            Shutdown(exitCode);
            return;
        }

        try
        {
            StartupElevationResult elevation = StartupElevation.Ensure(e.Args,
                VisDir.Core.ScanCoordinator.IsElevated(), Environment.ProcessPath
                    ?? throw new InvalidOperationException("The application path is unavailable."), Environment.CurrentDirectory,
                reportHealthyStart: UpdateService.HasPendingStartupHealthCheck ? UpdateService.ReportHealthyStart : null);
            if (elevation != StartupElevationResult.Continue)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[Startup elevation] {ex}");
            MessageBox.Show($"VisDir could not start with administrator access.\n\n{ex.Message}",
                "Unable to start VisDir", MessageBoxButton.OK, MessageBoxImage.Error);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(1);
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

    internal static void SafeLog(string message)
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

