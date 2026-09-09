using System.Diagnostics;
using VisDir.App.Update;
using Xunit;

namespace VisDir.Core.Tests;

/// <summary>Regression tests for the v1.2.0 updater sharing-violation failure:
/// concurrent downloads shared one exclusively-locked partial path.</summary>
public class UpdaterDownloadPathTests
{
    [Fact]
    public void PartialPaths_AreUniquePerAttempt()
    {
        string zip = Path.Combine(Path.GetTempPath(), "VisDir-9.9.9.zip");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 25; i++)
            Assert.True(seen.Add(UpdateService.NewUniquePartialPath(zip)), "partial paths must never repeat");
    }

    [Fact]
    public void PartialPath_StaysBesideFinalZipWithPartialSuffix()
    {
        string zip = Path.Combine(Path.GetTempPath(), "VisDir-1.2.1.zip");
        string partial = UpdateService.NewUniquePartialPath(zip);
        Assert.StartsWith(zip + ".", partial, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".partial", partial, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetDirectoryName(zip), Path.GetDirectoryName(partial));
    }

    [Fact]
    public async Task FileLockRetry_SucceedsAfterTransientSharingViolation()
    {
        int calls = 0;
        string result = await UpdateService.WithFileLockRetryAsync(() =>
        {
            if (++calls < 3)
                throw new IOException("locked", unchecked((int)0x80070020));
            return "ok";
        }, CancellationToken.None);
        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task FileLockRetry_PassesThroughNonLockErrors()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateService.WithFileLockRetryAsync<string>(
                () => throw new InvalidDataException("nope"), CancellationToken.None));
    }

    [Fact]
    public async Task FileLockRetry_BeatsRealHeldReadHandle()
    {
        // Simulates an antivirus/indexer holding the finished file: exclusive open
        // fails until the holder releases, then the retry envelope must succeed.
        string path = Path.Combine(Path.GetTempPath(), $"visdir-locktest-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "payload");
        try
        {
            using var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var releaser = Task.Run(async () =>
            {
                await Task.Delay(800);
                locker.Dispose();
            });
            using var winner = await UpdateService.WithFileLockRetryAsync(
                () => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None),
                CancellationToken.None);
            await releaser;
            Assert.True(winner.CanWrite);
        }
        finally
        {
            UpdateService.TryDeleteFileWithRetry(path);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task TryDeleteFileWithRetry_RemovesAfterRelease()
    {
        string path = Path.Combine(Path.GetTempPath(), $"visdir-deltest-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "payload");
        var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var releaser = Task.Run(async () =>
        {
            await Task.Delay(300);
            locker.Dispose();
        });
        UpdateService.TryDeleteFileWithRetry(path);
        await releaser;
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void StagedSignatures_UnsignedRunning_AcceptsUnsignedStaged()
    {
        string dir = Path.Combine(Path.GetTempPath(), "visdir-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prev = UpdateService.TrustChainValid;
        UpdateService.TrustChainValid = static _ => false;
        try
        {
            File.WriteAllText(Path.Combine(dir, "VisDir.App.exe"), "unsigned");
            File.WriteAllText(Path.Combine(dir, "VisDir.Scanner.dll"), "unsigned");
            UpdateService.VerifyStagedSignatures(dir);
        }
        finally
        {
            UpdateService.TrustChainValid = prev;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StagedSignatures_SignedExpectation_StillRefusesUnsignedStaged()
    {
        string dir = Path.Combine(Path.GetTempPath(), "visdir-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prev = UpdateService.TrustChainValid;
        UpdateService.TrustChainValid = static _ => true;
        try
        {
            File.WriteAllText(Path.Combine(dir, "VisDir.App.exe"), "unsigned");
            File.WriteAllText(Path.Combine(dir, "VisDir.Scanner.dll"), "unsigned");
            Assert.Throws<InvalidDataException>(() => UpdateService.VerifyStagedSignatures(dir));
        }
        finally
        {
            UpdateService.TrustChainValid = prev;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StagedSignaturesScript_UnsignedRunning_AcceptsUnsignedStaged()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "visdir-trustps-" + Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "installed");
        string staged = Path.Combine(root, "staged");
        try
        {
            Directory.CreateDirectory(installed);
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(installed, "VisDir.App.exe"), "unsigned");
            File.WriteAllText(Path.Combine(staged, "VisDir.App.exe"), "unsigned");
            File.WriteAllText(Path.Combine(staged, "VisDir.Scanner.dll"), "unsigned");
            string script = UpdateService.UpdateScriptForTests;
            const string marker = "function Test-StagedSignatures";
            int start = script.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, "Test-StagedSignatures not found in update script");
            int end = script.IndexOf("\nfunction ", start, StringComparison.Ordinal);
            Assert.True(end > start, "Test-StagedSignatures end not found");
            string harness = "$plan = [pscustomobject]@{ AppDir='" + installed + "'; RelativeExe='VisDir.App.exe'; ExpectedSignerThumbprint='' }\n"
                + script.Substring(start, end - start)
                + "\nTest-StagedSignatures '" + staged + "'\n";
            string ps1 = Path.Combine(root, "harness.ps1");
            File.WriteAllText(ps1, harness);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(ps1);
            using Process p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "PowerShell harness timed out.");
            Assert.True(p.ExitCode == 0, "Gate should accept when running exe is unsigned: " + stderr);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TestAccessDenied_DetectsAccessDeniedExceptions()
    {
        if (!OperatingSystem.IsWindows()) return;
        string script = UpdateService.UpdateScriptForTests;
        const string marker = "function Test-AccessDenied";
        int start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Test-AccessDenied not found in update script");
        int end = script.IndexOf("\nfunction ", start, StringComparison.Ordinal);
        Assert.True(end > start, "Test-AccessDenied end not found");

        string harness = script.Substring(start, end - start) + @"
$ex1 = New-Object System.UnauthorizedAccessException 'Access denied'
$ex2 = New-Object System.IO.IOException 'Locked', -2147024891
$ex3 = New-Object System.Exception 'Wrapper', $ex1
$ex4 = New-Object System.IO.FileNotFoundException 'Not found'

if (-not (Test-AccessDenied $ex1)) { throw 'Failed on UnauthorizedAccessException' }
if (-not (Test-AccessDenied $ex2)) { throw 'Failed on HResult -2147024891' }
if (-not (Test-AccessDenied $ex3)) { throw 'Failed on inner UnauthorizedAccessException' }
if (Test-AccessDenied $ex4) { throw 'Wrongly flagged FileNotFoundException' }
";
        string root = Path.Combine(Path.GetTempPath(), "visdir-accessps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ps1 = Path.Combine(root, "harness.ps1");
            File.WriteAllText(ps1, harness);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(ps1);
            using Process p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "PowerShell harness timed out.");
            Assert.True(p.ExitCode == 0, "Test-AccessDenied failed: " + stderr);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void UpdateSwap_ExecutesFullDirectorySwapWithSubdirectories()
    {
        if (!OperatingSystem.IsWindows()) return;
        string tempRoot = Path.Combine(Path.GetTempPath(), "VisDir", "Updates");
        Directory.CreateDirectory(tempRoot);
        string operationId = Guid.NewGuid().ToString("N");
        string staged = Path.Combine(tempRoot, $"staged-{operationId}");
        string appParent = Path.Combine(Path.GetTempPath(), $"visdir-app-{operationId}");
        string appDir = Path.Combine(appParent, "App");
        string planPath = Path.Combine(tempRoot, $"apply-update-{operationId}.json");
        string markerPath = Path.Combine(tempRoot, $"healthy-{operationId}.marker");

        try
        {
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(staged);
            Directory.CreateDirectory(Path.Combine(staged, "runtimes"));

            File.WriteAllText(Path.Combine(appDir, "VisDir.App.exe"), "v1-app");
            File.WriteAllText(Path.Combine(staged, "VisDir.App.exe"), "v2-app");
            File.WriteAllText(Path.Combine(staged, "VisDir.Scanner.dll"), "v2-scanner");
            File.WriteAllText(Path.Combine(staged, "runtimes", "sub.dll"), "v2-sub");

            string planJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                AppDir = appDir,
                StagedDir = staged,
                RelativeExe = "VisDir.App.exe",
                ProcessId = 999999,
                OperationId = operationId,
                MarkerPath = markerPath,
                ExpectedSignerThumbprint = ""
            });
            File.WriteAllText(planPath, planJson);

            // Test execution of Invoke-UpdateSwap up to the swap (excluding starting the process)
            string script = UpdateService.UpdateScriptForTests;
            string harness = "$plan = Get-Content -LiteralPath '" + planPath.Replace("'", "''") + "' -Raw | ConvertFrom-Json\n"
                + "$PlanPath = '" + planPath.Replace("'", "''") + "'\n"
                + script.Substring(script.IndexOf("function Test-AccessDenied", StringComparison.Ordinal))
                + "\nInvoke-UpdateSwap\n";

            // Replace the Start-Process line in harness with a mock that creates the marker
            harness = harness.Replace(
                "$newProcess = Start-Process -FilePath (Join-Path $appDir $plan.RelativeExe) -PassThru",
                "Set-Content -LiteralPath $plan.MarkerPath 'mock-healthy'\n    $newProcess = [pscustomobject]@{ Id = 1; HasExited = $false }");

            string testPs1 = Path.Combine(tempRoot, $"test-swap-{operationId}.ps1");
            File.WriteAllText(testPs1, harness);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(testPs1);

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "PowerShell harness timed out.");
            Assert.True(p.ExitCode == 0, $"Swap failed with exit code {p.ExitCode}:\n{stdout}\n{stderr}");

            Assert.Equal("v2-app", File.ReadAllText(Path.Combine(appDir, "VisDir.App.exe")));
            Assert.Equal("v2-scanner", File.ReadAllText(Path.Combine(appDir, "VisDir.Scanner.dll")));
            Assert.Equal("v2-sub", File.ReadAllText(Path.Combine(appDir, "runtimes", "sub.dll")));
        }
        finally
        {
            try { Directory.Delete(appParent, true); } catch { }
            try { Directory.Delete(staged, true); } catch { }
            try { File.Delete(planPath); } catch { }
            try { File.Delete(markerPath); } catch { }
        }
    }
}
