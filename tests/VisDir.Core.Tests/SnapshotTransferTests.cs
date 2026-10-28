using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VisDir.App.Scan;
using VisDir.Scanner;

namespace VisDir.Core.Tests;

public sealed class SnapshotTransferTests
{
    [Fact]
    public async Task LargeWorkerResponseUsesBatchedPipeIoAndPreservesFramingAndSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int count = 100_000;
        ScanResult expected = BuildSnapshot(count);
        string name = "visdir-transfer-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var stopReader = timeout.Token.Register(server.Dispose);
        using var stopWriter = timeout.Token.Register(client.Dispose);
        Task connection = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connection;
        using var input = new CountingStream(server);
        using var output = new CountingStream(client);
        var updates = new List<ScanProgress>();
        Task writing = Task.Run(() =>
        {
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            // A shared read buffer must not lose the Result marker or snapshot bytes
            // prefetched while decoding this preceding JSON progress message.
            writer.Write(WorkerHost.Progress);
            writer.Write(JsonSerializer.Serialize(new ScanProgress(count, 1, expected.Root.TotalAllocated, 123, -1, "Scanned")));
            writer.Write(WorkerHost.Result);
            writer.Flush();
            TreeSerializer.Write(output, expected, timeout.Token);
        }, timeout.Token);

        ScanResult actual = await Task.Run(() => ScanService.ReadWorkerResponse(input, timeout.Token,
            new CaptureProgress(updates.Add), startupMs: 12), timeout.Token);
        await writing;
        Assert.True(input.BytesRead > 256 * 1024);
        // Verify syscall batching instead of imposing a machine-dependent time limit.
        // The regression made ~10 reads and two forced writes for every tiny node.
        Assert.True(input.ReadCalls <= input.BytesRead / 1024 + 100,
            $"{input.ReadCalls:N0} pipe reads for {input.BytesRead:N0} bytes: response reads must be buffered.");
        Assert.True(output.WriteCalls <= output.BytesWritten / 4096 + 32,
            $"{output.WriteCalls:N0} pipe writes for {output.BytesWritten:N0} bytes: node writes must not flush individually.");
        Assert.True(server.CanRead); // The response reader does not own the caller's connection.
        Assert.Equal(count, actual.Root.Children!.Count);
        Assert.Equal(expected.Root.TotalAllocated, actual.Root.TotalAllocated);
        Assert.Equal(expected.Root.TotalStreamAllocated, actual.Root.TotalStreamAllocated);
        Assert.Equal(expected.Root.TotalMetadataAllocated, actual.Root.TotalMetadataAllocated);
        Assert.Equal(expected.Stats.FileCount, actual.Stats.FileCount);
        Assert.Equal(expected.AccountingFeatures, actual.AccountingFeatures);
        Assert.True(actual.Root.IsFrozen);
        for (int i = 0; i < count; i += 7919)
        {
            FsNode before = expected.Root.Children![i], after = actual.Root.Children[i];
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.Identity, after.Identity);
            Assert.Equal(before.AllocatedSize, after.AllocatedSize);
            Assert.Equal(before.StreamAllocatedSize, after.StreamAllocatedSize);
        }
        Assert.Contains(updates, p => p.Phase == "Scanned" && p.FilesSeen == count);
        Assert.Contains(updates, p => p.Phase == "Loading results" && p.FilesSeen == count && p.DirsSeen == 1);
        Assert.Equal(12, actual.Stats.PhaseMs["broker-startup"]);
        Assert.True(actual.Stats.PhaseMs["snapshot-load"] >= 0);
    }

    [Fact]
    public async Task CancellationClosesPipeBlockedInsideSnapshotLoading()
    {
        if (!OperatingSystem.IsWindows()) return;
        string name = "visdir-transfer-cancel-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await connection;
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(server.Dispose);
        var waitingForSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new CountingStream(server, call =>
        {
            if (call == 2) waitingForSnapshot.TrySetResult();
        });
        Task<ScanResult> reading = Task.Run(() => ScanService.ReadWorkerResponse(input, cancellation.Token,
            new CaptureProgress(_ => { }), startupMs: 0));
        client.WriteByte(WorkerHost.Result);
        client.Flush();
        // The first read consumes the marker. The second waits for snapshot bytes
        // from a still-connected peer, matching a stalled or cancelled helper.
        await waitingForSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Exception? error = await Record.ExceptionAsync(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cancellation.IsCancellationRequested);
        // RunWorkerAsync translates transport/truncation errors to cancellation
        // once its token is set; the lower-level reader only has to unblock here.
        Assert.True(error is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException, error?.ToString());
    }

    [Fact]
    public async Task DisconnectedWorkerWithPartialSnapshotFailsInsteadOfHanging()
    {
        if (!OperatingSystem.IsWindows()) return;
        string name = "visdir-transfer-short-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await connection;
        Task<ScanResult> reading = Task.Run(() => ScanService.ReadWorkerResponse(server, CancellationToken.None,
            new CaptureProgress(_ => { }), startupMs: 0));
        using (var writer = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(WorkerHost.Result);
            writer.Write(TreeSerializer.Magic); // Deliberately missing the version and tree.
            writer.Flush();
        }
        client.Dispose();
        await Assert.ThrowsAsync<InvalidDataException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static ScanResult BuildSnapshot(int count)
    {
        var root = new FsNode
        {
            Name = "C:\\", Flags = NodeFlags.Directory, VolumeKey = 17,
            AllocatedSize = 8192, MetadataAllocatedSize = 8192,
        };
        root.EnsureCapacity(count);
        for (int i = 0; i < count; i++)
        {
            ulong streams = i % 257 == 0 ? 4096UL : 0;
            root.AddChild(new FsNode
            {
                Name = $"file-{i:D8}.bin", LogicalSize = 3000, AllocatedSize = 4096 + streams,
                StreamAllocatedSize = streams, FileKey = i + 100, FileKeyHigh = (ulong)(i % 3), VolumeKey = 17,
            });
        }
        TreeOps.Finalize(root);
        return new ScanResult
        {
            Root = root, Volume = new VolumeInfo { RootPath = "C:\\", DisplayName = "Synthetic", VolumeSerialNumber = 17 },
            Stats = new ScanStats { FileCount = count, DirectoryCount = 1, BytesSeen = root.TotalAllocated },
            EngineName = "mft", RequestedEngine = "mft",
            AccountingFeatures = AccountingFeatures.UnnamedData | AccountingFeatures.NamedStreams |
                AccountingFeatures.DirectoryMetadata | AccountingFeatures.FileIdentity,
        };
    }

    private sealed class CaptureProgress(Action<ScanProgress> callback) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => callback(value);
    }

    private sealed class CountingStream(Stream inner, Action<long>? reading = null) : Stream
    {
        public long ReadCalls, WriteCalls, BytesRead, BytesWritten;
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            long call = Interlocked.Increment(ref ReadCalls);
            reading?.Invoke(call);
            int read = inner.Read(buffer);
            Interlocked.Add(ref BytesRead, read);
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Interlocked.Increment(ref WriteCalls);
            inner.Write(buffer);
            Interlocked.Add(ref BytesWritten, buffer.Length);
        }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
