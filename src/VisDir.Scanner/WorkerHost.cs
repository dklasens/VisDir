using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using VisDir.Core;
using Microsoft.Win32.SafeHandles;

namespace VisDir.Scanner;

public sealed record WorkerRequest(string Path, string Mode, int Threads = 0);

/// <summary>Read-only scanner broker. No output path or arbitrary command is accepted.</summary>
[SupportedOSPlatform("windows")]
public static class WorkerHost
{
    public const byte Progress = 1, Result = 2, Error = 3, Cancelled = 4;

    public static int Execute(string pipeName, int expectedServerProcessId)
    {
        if (expectedServerProcessId <= 0 || !pipeName.StartsWith("visdir-", StringComparison.Ordinal) || pipeName.Length > 100
            || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return 1;
        try { return RunAsync(pipeName, expectedServerProcessId).GetAwaiter().GetResult(); }
        catch (IOException) { return 2; } // Parent closed the session.
        catch (OperationCanceledException) { return 2; }
        catch (UnauthorizedAccessException) { return 2; }
        catch (TimeoutException) { return 2; }
    }

    private static async Task<int> RunAsync(string pipeName, int expectedServerProcessId)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(30_000).ConfigureAwait(false);
        VerifyServer(pipe, expectedServerProcessId);
        using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
        WorkerRequest request = JsonSerializer.Deserialize<WorkerRequest>(reader.ReadString())
            ?? throw new InvalidDataException("Missing scan request.");
        using var cancellation = new CancellationTokenSource();
        // Reading the opposite pipe direction also detects a crashed/closed UI.
        Task disconnect = Task.Run(async () =>
        {
            try { await pipe.ReadAsync(new byte[1], cancellation.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { await cancellation.CancelAsync().ConfigureAwait(false); }
        });
        var outputLock = new object();
        var progress = new CallbackProgress(value =>
        {
            lock (outputLock)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                writer.Write(Progress);
                writer.Write(JsonSerializer.Serialize(value));
                writer.Flush();
            }
        });
        try
        {
            ScanResult result = new ScanCoordinator().Run(new ScanOptions { Path = request.Path, Threads = request.Threads },
                request.Mode, cancellation.Token, progress);
            lock (outputLock)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                writer.Write(Result);
                writer.Flush();
                TreeSerializer.Write(pipe, result, cancellation.Token);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            lock (outputLock) { writer.Write(Cancelled); writer.Flush(); }
            return 2;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (outputLock) { writer.Write(Error); writer.Write(ex.Message); writer.Flush(); }
            return 3;
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await disconnect.ConfigureAwait(false);
        }
    }

    internal static void VerifyServer(NamedPipeClientStream pipe, int expectedServerProcessId)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != expectedServerProcessId)
            throw new UnauthorizedAccessException("The scanner pipe does not belong to the application that launched it.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    private sealed class CallbackProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
