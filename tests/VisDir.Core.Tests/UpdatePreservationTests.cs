using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VisDir.App.Update;

namespace VisDir.Core.Tests;

public sealed class UpdatePreservationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ManifestUpdate_PreservesUnownedFilesAndRollsBackFailures(bool startupFails, bool corruptPayload, bool foreignModulePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        string operation = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "visdir-owned-" + operation);
        string installed = Path.Combine(root, "installed");
        string staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(staged);
        try
        {
            Write(installed, "VisDir.App.exe", "old app");
            Write(installed, "VisDir.Scanner.dll", "old scanner");
            Write(installed, "obsolete.dll", "old library");
            Write(installed, "edited.config", "original config");
            Manifest(installed);
            Write(installed, "edited.config", "user modified config");
            Write(installed, "reports\\personal.txt", "user report");
            Write(installed, "other-app\\program.exe", "unrelated program");
            Write(installed, "runtimes\\new.dll", "previously unowned file");
            Write(staged, "VisDir.App.exe", "new app");
            Write(staged, "VisDir.Scanner.dll", "new scanner");
            Write(staged, "runtimes\\new.dll", "new library");
            Manifest(staged);
            if (corruptPayload) Write(staged, "VisDir.App.exe", "changed after verification");
            string planPath = Path.Combine(root, "plan.json");
            string marker = Path.Combine(root, "healthy.marker");
            File.WriteAllText(planPath, JsonSerializer.Serialize(new
            {
                AppDir = installed, StagedDir = staged, RelativeExe = "VisDir.App.exe",
                ProcessId = int.MaxValue, OperationId = operation, MarkerPath = marker,
                ExpectedSignerThumbprint = "",
            }));
            string script = UpdateService.UpdateScriptForTests.Replace("\r\n", "\n");
            int start = script.IndexOf("function Write-UpdateLog", StringComparison.Ordinal);
            int end = script.IndexOf("\ntry {\n    Invoke-UpdateSwap", StringComparison.Ordinal);
            string harness = "$ErrorActionPreference = 'Stop'\n$PlanPath = '" + Quote(planPath) + "'\n"
                + "$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json\n"
                + script[start..end];
            harness = harness.Replace(
                "$newProcess = Start-Process -FilePath (Join-Path $appDir $plan.RelativeExe) -PassThru",
                startupFails
                    ? "$newProcess = [pscustomobject]@{ Id = 1; HasExited = $true }"
                    : "Set-Content -LiteralPath $plan.MarkerPath 'healthy'\n        $newProcess = [pscustomobject]@{ Id = 1; HasExited = $false }");
            bool fails = startupFails || corruptPayload;
            string expectedError = corruptPayload ? "*Update checksum failed*" : "*did not report a healthy startup*";
            harness += fails
                ? "\ntry { Invoke-UpdateSwap; throw 'Expected update failure' } catch { if ($_.Exception.Message -notlike '" + expectedError + "') { throw } }"
                : "\nInvoke-UpdateSwap";
            string harnessPath = Path.Combine(root, "harness.ps1");
            File.WriteAllText(harnessPath, harness);
            string? foreignModules = null;
            if (foreignModulePath)
            {
                // A parent PowerShell 7 session can put incompatible modules ahead of
                // Windows PowerShell's built-ins, as on GitHub's Windows runners.
                foreignModules = Path.Combine(root, "foreign-modules");
                Write(foreignModules, @"Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1",
                    "@{ RootModule = 'broken.psm1'; ModuleVersion = '99.0.0'; FunctionsToExport = @('Get-AuthenticodeSignature') }");
                Write(foreignModules, @"Microsoft.PowerShell.Security\broken.psm1",
                    "function Get-AuthenticodeSignature { throw 'Incompatible inherited security module' }");
                // Set inside the child as well: Windows PowerShell may reorder the
                // inherited module path during its own initialization.
                File.WriteAllText(harnessPath, "$env:PSModulePath = '" + Quote(foreignModules) +
                    ";' + $env:PSModulePath\nfunction Get-FileHash { throw 'Incompatible inherited hashing module' }\n" + harness);
            }
            RunPowerShell(harnessPath, foreignModules);

            Assert.Equal("user report", File.ReadAllText(Path.Combine(installed, "reports", "personal.txt")));
            Assert.Equal("unrelated program", File.ReadAllText(Path.Combine(installed, "other-app", "program.exe")));
            Assert.Equal("user modified config", File.ReadAllText(Path.Combine(installed, "edited.config")));
            Assert.Equal(fails ? "old app" : "new app", File.ReadAllText(Path.Combine(installed, "VisDir.App.exe")));
            Assert.Equal(fails ? "old scanner" : "new scanner", File.ReadAllText(Path.Combine(installed, "VisDir.Scanner.dll")));
            Assert.Equal(fails, File.Exists(Path.Combine(installed, "obsolete.dll")));
            Assert.Equal(fails ? "previously unowned file" : "new library", File.ReadAllText(Path.Combine(installed, "runtimes", "new.dll")));
            if (!fails)
                Assert.Equal("previously unowned file", File.ReadAllText(Path.Combine(installed, ".visdir-preserved-" + operation, "runtimes", "new.dll")));
            Assert.Empty(Directory.GetDirectories(installed, ".visdir-update-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Quote(string path) => path.Replace("'", "''");

    private static void Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Manifest(string root)
    {
        var lines = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}  {Path.GetRelativePath(root, path)}");
        File.WriteAllLines(Path.Combine(root, "checksums-win-x64.sha256"), lines);
    }

    private static void RunPowerShell(string path, string? foreignModules = null)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
            psi.ArgumentList.Add(argument);
        if (foreignModules is not null)
            psi.Environment["PSModulePath"] = foreignModules + Path.PathSeparator + Environment.GetEnvironmentVariable("PSModulePath");
        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Update harness timed out.");
        }
        Assert.True(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
