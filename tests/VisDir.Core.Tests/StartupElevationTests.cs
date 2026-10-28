using System.ComponentModel;
using VisDir.App;

namespace VisDir.Core.Tests;

public sealed class StartupElevationTests
{
    [Fact]
    public void InteractiveStartupRequestsElevationAndPreservesArguments()
    {
        string[] arguments = ["--scan", @"C:\Folder with spaces\", "--fast"];
        int launches = 0;
        var result = StartupElevation.Ensure(arguments, false, @"C:\Program Files\VisDir\VisDir.App.exe", @"C:\Work", start =>
        {
            launches++;
            Assert.Equal("runas", start.Verb);
            Assert.True(start.UseShellExecute);
            Assert.Equal(@"C:\Program Files\VisDir\VisDir.App.exe", start.FileName);
            Assert.Equal(@"C:\Work", start.WorkingDirectory);
            Assert.Equal(arguments, start.ArgumentList);
            return true;
        });
        Assert.Equal(StartupElevationResult.Relaunched, result);
        Assert.Equal(1, launches);
    }

    [Theory]
    [InlineData(true, "--scan")]
    [InlineData(false, "--worker")]
    [InlineData(false, "--worker-pipe")]
    public void ElevatedAppAndHeadlessWorkersNeverPromptAgain(bool elevated, string argument)
    {
        Assert.Equal(StartupElevationResult.Continue,
            StartupElevation.Ensure([argument, "target"], elevated, "app.exe", "C:\\", _ =>
                throw new InvalidOperationException("An unnecessary elevation prompt was attempted.")));
    }

    [Fact]
    public void DecliningUacStopsStartupWithoutLaunchingAnUnelevatedWindow()
    {
        Assert.Equal(StartupElevationResult.Cancelled,
            StartupElevation.Ensure([], false, "app.exe", "C:\\", _ => throw new Win32Exception(1223)));
    }

    [Fact]
    public void LaunchErrorsAreNotMisreportedAsUserCancellation()
    {
        Assert.Throws<Win32Exception>(() => StartupElevation.Ensure([], false, "app.exe", "C:\\",
            _ => throw new Win32Exception(2)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdateHealthIsReportedOnlyAfterElevatedWindowBecomesReady(bool ready)
    {
        var events = new List<string>();
        StartupElevationResult result = StartupElevation.Ensure([], false, "app.exe", "C:\\",
            _ => { events.Add("launch-and-wait"); return ready; }, () => events.Add("healthy"));
        Assert.Equal(StartupElevationResult.Relaunched, result);
        Assert.Equal(ready ? new[] { "launch-and-wait", "healthy" } : new[] { "launch-and-wait" }, events);
    }

    [Fact]
    public void CancelledUpdateLaunchNeverReportsHealthyStartup()
    {
        bool healthy = false;
        Assert.Equal(StartupElevationResult.Cancelled,
            StartupElevation.Ensure([], false, "app.exe", "C:\\", _ => throw new Win32Exception(1223), () => healthy = true));
        Assert.False(healthy);
    }
}
