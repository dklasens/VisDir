using System.ComponentModel;
using System.Diagnostics;

namespace VisDir.App;

internal enum StartupElevationResult { Continue, Relaunched, Cancelled }

/// <summary>Elevates the interactive application once, before any window or scan starts.</summary>
internal static class StartupElevation
{
    internal static StartupElevationResult Ensure(string[] arguments, bool elevated, string executable,
        string workingDirectory, Func<ProcessStartInfo, bool>? launch = null, Action? reportHealthyStart = null)
    {
        // Headless workers inherit their caller's token and never create a UAC prompt.
        // Keeping the manifest asInvoker also allows CI and CLI callers to run them.
        if (elevated || arguments is ["--worker" or "--worker-pipe", ..])
            return StartupElevationResult.Continue;

        var start = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workingDirectory,
            UseShellExecute = true, Verb = "runas",
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            bool ready;
            if (launch is not null) ready = launch(start);
            else
            {
                using Process? child = Process.Start(start);
                if (child is null) throw new InvalidOperationException("Windows did not start the elevated application.");
                // An older updater monitors this bootstrap process, not the elevated
                // child. Keep it alive until the actual window is ready, then report
                // health using the original environment even if UAC did not inherit it.
                ready = reportHealthyStart is null || child.WaitForInputIdle(15_000);
                if (ready && reportHealthyStart is not null)
                {
                    child.Refresh();
                    ready = child.MainWindowHandle != IntPtr.Zero;
                }
            }
            if (ready) reportHealthyStart?.Invoke();
            return StartupElevationResult.Relaunched;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return StartupElevationResult.Cancelled;
        }
    }
}
