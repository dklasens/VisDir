using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using VisDir.Core.Interop;

namespace VisDir.Core;

internal sealed class DirectoryAccessDeniedException : Exception
{
    public DirectoryAccessDeniedException() : base("Access denied") { }
}

internal sealed class CorruptBatchException : Exception
{
    public CorruptBatchException() : base("Malformed directory enumeration batch") { }
}

internal sealed class CapabilityDowngradeException : Exception
{
    public CapabilityDowngradeException() : base("Volume rejected extended directory info class") { }
}

/// <summary>Raised when the scan exceeds hard sanity limits — indicates an engine bug, not user data.</summary>
public sealed class SafetyLimitException : Exception
{
    public SafetyLimitException(string message) : base(message) { }
}

/// <summary>
/// Portable fallback scanner: parallel directory walk using GetFileInformationByHandleEx
/// (FileIdExtdDirectoryInfo, downgrading to FileFullDirectoryInfo where unsupported).
/// No elevation required, works on any filesystem Windows can enumerate.
/// </summary>
public sealed class GenericScanner : IDiskScanner
{
    private const int InitialBufferSize = 1 << 20;   // 1 MiB
    private const int MaxBufferSize = 16 << 20;
    private const int MaxDepth = 512;
    private const long DefaultMaxDirs = 20_000_000;
    private const int WorkQueueCapacity = 50_000;

    private static readonly long MaxDirsSafetyLimit = LoadMaxDirs();
    private static readonly long[] SnapshotPoints = { 50_000, 150_000, 400_000, 1_000_000, 4_000_000 };

    private static long LoadMaxDirs() =>
        long.TryParse(Environment.GetEnvironmentVariable("VISDIR_MAX_DIRS"), out var v) && v > 0 ? v : DefaultMaxDirs;

    private long _filesSeen;
    private long _dirsSeen;
    private long _errors;
    private long _bytesSeen;
    private volatile bool _braked;
    private long _snapshotTaken;

    private int _active;                 // outstanding directories (seeded with 1)
    private volatile bool _cancelled;
    private bool _useExtdClass = true;   // sticky downgrade if volume rejects extd class
    private readonly ThreadLocal<byte[]?> _buffer = new(() => null);

    // Hardlink dedup: same FileId reached via multiple directories counts allocated
    // bytes once. FileId is only available from the extended info class (key==0 otherwise).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _seenFileIds = new();
    private static readonly bool TraceErrors =
        Environment.GetEnvironmentVariable("VISDIR_TRACE_ERRORS") == "1";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _errorCounts = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errorSamples = new();

    public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
    {
        ResetState();
        VolumeInfo volume = VolumeQuery.Query(options.Path);

        var root = new FsNode
        {
            Name = volume.RootPath,
            Flags = NodeFlags.Directory,
        };

        var sw = Stopwatch.StartNew();
        int threads = options.Threads > 0 ? Math.Min(options.Threads, 64) : Environment.ProcessorCount;

        var channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(WorkQueueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _active = 1;
        _dirsSeen = 1; // root
        channel.Writer.TryWrite(new Item(volume.RootPath, root, 0));

        var workers = new Task[threads];
        for (int i = 0; i < threads; i++)
            workers[i] = Task.Run(() => WorkerLoop(channel.Reader, channel.Writer, cancellationToken));
        var progressDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressTask = progress is null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!progressDone.Task.IsCompleted)
            {
                try { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                ulong bytes = (ulong)Interlocked.Read(ref _bytesSeen);
                ulong expected = volume.TotalBytes >= volume.FreeBytes ? volume.TotalBytes - volume.FreeBytes : 0;
                double fraction = expected > 0 ? Math.Min(0.98, (double)bytes / expected) : -1;
                progress.Report(new ScanProgress(
                    Interlocked.Read(ref _filesSeen),
                    Interlocked.Read(ref _dirsSeen),
                    bytes,
                    sw.Elapsed.TotalMilliseconds,
                    fraction,
                    "Walking folders"));
            }
        });

        try
        {
            Task.WaitAll(workers);
        }
        finally
        {
            progressDone.TrySetResult();
            try { progressTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_braked)
            throw new SafetyLimitException(
                $"Scan aborted: directory count exceeded safety limit ({MaxDirsSafetyLimit:N0}). " +
                "This indicates an engine defect — please report with VISDIR_TRACE_ERRORS=1 output.");

        if (root.Children is { Count: 1 } rootChildren &&
            (rootChildren[0].Flags & (NodeFlags.AccessDenied | NodeFlags.ErrorNode)) != 0)
        {
            throw new IOException($"The scan root could not be read: {volume.RootPath}");
        }

        if (TraceErrors && !_errorCounts.IsEmpty)
        {
            foreach (var kv in _errorCounts.OrderByDescending(k => k.Value).Take(20))
                Console.Error.WriteLine($"ERRCODE {kv.Key} x{kv.Value}");
            foreach (var s in _errorSamples.Take(10))
                Console.Error.WriteLine($"ERRSAMPLE {s}");
        }

        TreeOps.Finalize(root);

        var stats = new ScanStats
        {
            FileCount = Interlocked.Read(ref _filesSeen),
            DirectoryCount = Interlocked.Read(ref _dirsSeen),
            ErrorCount = Interlocked.Read(ref _errors),
            BytesSeen = (ulong)Interlocked.Read(ref _bytesSeen),
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
        return new ScanResult { Volume = volume, Root = root, Stats = stats, EngineName = "generic" };
    }

    private readonly record struct Item(string Path, FsNode Node, int Depth);

    private void ResetState()
    {
        _filesSeen = 0;
        _dirsSeen = 0;
        _errors = 0;
        _bytesSeen = 0;
        _active = 0;
        _braked = false;
        _cancelled = false;
        _snapshotTaken = 0;
        _useExtdClass = true;
        _seenFileIds.Clear();
        _errorCounts.Clear();
        while (_errorSamples.TryDequeue(out _)) { }
    }

    private async Task WorkerLoop(ChannelReader<Item> reader, ChannelWriter<Item> writer, CancellationToken ct)
    {
        while (!_cancelled)
        {
            Item item;
            try
            {
                item = await reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            await HandleItem(item, writer, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleItem(Item item, ChannelWriter<Item> writer, CancellationToken ct)
    {
        try
        {
            await Process(item.Path, item.Node, item.Depth, writer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cancelled = true;
            writer.TryComplete();
        }
        catch (DirectoryAccessDeniedException)
        {
            RecordError("access_denied");
            Interlocked.Increment(ref _errors);
            item.Node.AddChild(new FsNode
            {
                Name = "[access denied]",
                Flags = NodeFlags.AccessDenied | NodeFlags.Directory,
            });
        }
        catch (Exception ex)
        {
            int code = ex is Win32Exception wex ? wex.NativeErrorCode : -1;
            RecordError($"{ex.GetType().Name}/{code}", item.Path);
            Interlocked.Increment(ref _errors);
            item.Node.AddChild(new FsNode
            {
                Name = "[unreadable]",
                Flags = NodeFlags.ErrorNode | NodeFlags.Directory,
            });
        }
        finally
        {
            if (Interlocked.Decrement(ref _active) == 0 && !_cancelled)
                writer.TryComplete();
        }
    }

    private void RecordError(string code, string? sample = null)
    {
        _errorCounts.AddOrUpdate(code, 1, (_, c) => c + 1);
        if (TraceErrors)
        {
            // Stream immediately so diagnostics survive an aborted/killed run.
            Console.Error.WriteLine(sample is null ? $"ERR {code}" : $"ERR {code} :: {sample}");
        }
        else if (sample is not null && _errorSamples.Count < 40 && !_errorSamples.Contains(sample))
        {
            _errorSamples.Enqueue(sample);
        }
    }

    private async Task Process(string dirPath, FsNode dirNode, int depth, ChannelWriter<Item> writer, CancellationToken ct)
    {
        List<FsNode> children = Enumerate(dirPath, ct);

        foreach (FsNode child in children)
        {
            dirNode.AddChild(child);

            if ((child.Flags & NodeFlags.Directory) == 0) continue;
            if ((child.Flags & NodeFlags.ReparsePoint) != 0) continue; // never follow junctions/symlinks
            if (depth + 1 >= MaxDepth)
            {
                RecordError("max_depth", dirPath);
                continue;
            }

            // Keep the queue bounded. If every worker is producing while it is full, process
            // the child inline so producers cannot deadlock waiting for themselves to read.
            Interlocked.Increment(ref _active);
            var next = new Item(PathUtils.JoinDir(dirPath, child.Name), child, depth + 1);
            if (!writer.TryWrite(next))
                await HandleItem(next, writer, ct).ConfigureAwait(false);
        }

        if (children.Count > 20_000)
            RecordError("huge_fanout", $"{dirPath} -> {children.Count} entries");

        long dirs = Interlocked.Read(ref _dirsSeen);
        for (int i = 0; i < SnapshotPoints.Length; i++)
        {
            if (dirs >= SnapshotPoints[i])
            {
                long bit = 1L << i;
                if ((Interlocked.Or(ref _snapshotTaken, bit) & bit) == 0)
                    RecordError($"SNAPSHOT_dirs_{dirs}", $"depth={depth} path={dirPath}");
            }
        }

        if (dirs > MaxDirsSafetyLimit)
        {
            _braked = true;
            _cancelled = true;
            writer.TryComplete();
        }
    }

    /// <summary>
    /// Enumerates one directory; recovers from malformed batches by retrying THIS directory
    /// with the fallback info class. The capability downgrade (API rejects the class) stays
    /// global, but a shape-mismatch never poisons parsing for other directories.
    /// </summary>
    private List<FsNode> Enumerate(string dirPath, CancellationToken ct)
    {
        try
        {
            return EnumerateCore(dirPath, extdRequested: true, ct);
        }
        catch (CorruptBatchException)
        {
            RecordError("extd_corrupt_retry_full", dirPath);
            try
            {
                return EnumerateCore(dirPath, extdRequested: false, ct);
            }
            catch (CorruptBatchException)
            {
                // Both layouts failed validation — treat as unreadable rather than guessing.
                throw new Win32Exception(NativeMethods.ERROR_INVALID_PARAMETER);
            }
        }
        catch (CapabilityDowngradeException)
        {
            // Class rejected mid-enumeration: restart cleanly with the fallback layout
            // so streams are never mixed on one handle.
            return EnumerateCore(dirPath, extdRequested: false, ct);
        }
    }

    /// <summary>Enumerates one directory, returning fully-built child nodes.</summary>
    private unsafe List<FsNode> EnumerateCore(string dirPath, bool extdRequested, CancellationToken ct)
    {
        IntPtr hDir = NativeMethods.CreateFileW(
            PathUtils.Extend(dirPath),
            NativeMethods.FILE_LIST_DIRECTORY,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (hDir == (IntPtr)(-1))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == NativeMethods.ERROR_ACCESS_DENIED)
                throw new DirectoryAccessDeniedException();
            throw new Win32Exception(err);
        }

        var result = new List<FsNode>(128);
        byte[] buf = _buffer.Value ??= new byte[InitialBufferSize];
        // Capture the information class for this handle. A different worker may
        // discover a volume-wide downgrade while this directory is in flight; it
        // must not make us switch record layouts halfway through one enumeration.
        bool useExtd = extdRequested && Volatile.Read(ref _useExtdClass);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_braked) throw new OperationCanceledException();

                bool ok;
                fixed (byte* p = buf)
                {
                    ok = NativeMethods.GetFileInformationByHandleEx(
                        hDir,
                        useExtd ? NativeMethods.FileIdExtdDirectoryInfoClass : NativeMethods.FileFullDirectoryInfoClass,
                        (IntPtr)p,
                        (uint)buf.Length);
                }

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ERROR_NO_MORE_FILES) break;
                    if (err is NativeMethods.ERROR_INVALID_PARAMETER or NativeMethods.ERROR_NOT_SUPPORTED && useExtd)
                    {
                        // Restart this directory under the fallback layout — never mix classes
                        // on one handle (enumeration position semantics differ per class).
                        // finally-block closes the handle; wrapper restarts cleanly.
                        Volatile.Write(ref _useExtdClass, false);
                        throw new CapabilityDowngradeException();
                    }
                    if (err == NativeMethods.ERROR_ACCESS_DENIED)
                        throw new DirectoryAccessDeniedException();
                    throw new Win32Exception(err);
                }

                int parsed;
                fixed (byte* p = buf)
                {
                    parsed = ParseBatch(p, buf.Length, result, useExtd);
                }

                if (parsed < 0) throw new CorruptBatchException();
                if (parsed == 0) break; // defensive: no forward progress possible

                if (parsed > 2048 && buf.Length < MaxBufferSize)
                {
                    var bigger = new byte[Math.Min(buf.Length * 2, MaxBufferSize)];
                    _buffer.Value = bigger;
                    buf = bigger;
                }
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hDir);
        }

        return result;
    }

    private unsafe int ParseBatch(byte* basePtr, int bufferLength, List<FsNode> sink, bool extd)
    {
        int nameOffset = extd ? NativeMethods.Extd_FixedSize : NativeMethods.Full_FixedSize;
        int offLogical = extd ? NativeMethods.Extd_EndOfFile : NativeMethods.Full_EndOfFile;
        int offAllocated = extd ? NativeMethods.Extd_AllocationSize : NativeMethods.Full_AllocationSize;
        int offAttributes = extd ? NativeMethods.Extd_Attributes : NativeMethods.Full_Attributes;
        int offNameBytes = extd ? NativeMethods.Extd_NameLengthBytes : NativeMethods.Full_NameLengthBytes;

        byte* cur = basePtr;
        byte* end = basePtr + bufferLength;
        int count = 0;

        while (cur + nameOffset <= end)
        {
            uint next = *(uint*)cur;

            // Strict structural validation — a violation means the batch does not match the
            // assumed record layout. Return -1 so the caller can restart with the fallback class.
            if ((next & 7) != 0) return -1;                                  // records are 8-byte aligned
            if (next != 0 && (next < (uint)nameOffset + 2 || cur + next > end)) return -1;
            if (next == 0 && cur + nameOffset > end) return -1;

            uint nameBytes = *(uint*)(cur + offNameBytes);
            if (nameBytes == 0 || (nameBytes & 1) != 0 || nameBytes > 65536) return -1; // never a legal name
            if (cur + nameOffset + nameBytes > end) return -1;

            char* namePtr = (char*)(cur + nameOffset);

            // NT-level enumeration surfaces "." and ".." on many volumes; skip them
            // before allocating anything.
            bool isDotEntry =
                (nameBytes == 2 && *namePtr == '.') ||
                (nameBytes == 4 && namePtr[0] == '.' && namePtr[1] == '.');
            if (isDotEntry)
            {
                if (next == 0) break;
                cur += next;
                continue;
            }

            long logical = *(long*)(cur + offLogical);
            long allocated = *(long*)(cur + offAllocated);
            long fileKey = extd ? *(long*)(cur + NativeMethods.Extd_FileId) : 0;

            int nameLen = (int)(nameBytes / 2);

            // Embedded NULs indicate we are reading a record with the wrong layout —
            // never enqueue such names (they produce phantom paths downstream).
            bool hasNul = false;
            for (int ci = 0; ci < nameLen; ci++)
            {
                if (namePtr[ci] == '\0') { hasNul = true; break; }
            }
            if (hasNul)
            {
                RecordError("nul_name", extd ? "extd" : "full");
                if (next == 0) break;
                cur += next;
                continue;
            }

            var node = new FsNode
            {
                LogicalSize = (ulong)Math.Max(0L, logical),
                AllocatedSize = (ulong)Math.Max(0L, allocated),
                FileKey = fileKey,
                Name = new string(namePtr, 0, nameLen),
            };

            bool isDir = ApplyAttributes(node, *(uint*)(cur + offAttributes),
                extd ? *(uint*)(cur + NativeMethods.Extd_ReparsePointTag) : 0);

            bool hardlinkDup = false;
            if (!isDir && fileKey != 0)
                hardlinkDup = !_seenFileIds.TryAdd(fileKey, 0);
            if (hardlinkDup)
            {
                node.Flags |= NodeFlags.Hardlinked;
                node.AllocatedSize = 0;
            }

            sink.Add(node);
            count++;

            if (isDir)
            {
                long dirs = Interlocked.Increment(ref _dirsSeen);
                if (dirs > MaxDirsSafetyLimit)
                {
                    _braked = true;
                    _cancelled = true;
                }
            }
            else
            {
                Interlocked.Increment(ref _filesSeen);
                Interlocked.Add(ref _bytesSeen, (long)node.AllocatedSize);
            }

            if (next == 0) break; // 0 marks the last entry in this batch
            cur += next;
        }
        return count;
    }

    /// <summary>
    /// Sets flag bits; returns true when the entry is a directory.
    /// Note: flags apply to files AND directories alike — junctions/symlinked dirs must
    /// carry ReparsePoint or they would be traversed (double counting, cycles).
    /// </summary>
    private static bool ApplyAttributes(FsNode node, uint attrs, uint reparseTag)
    {
        const uint HIDDEN = 0x2;
        const uint SYSTEM = 0x4;
        const uint DIRECTORY = 0x10;
        const uint SPARSE = 0x200;
        const uint REPARSE = 0x400;
        const uint COMPRESSED = 0x800;
        const uint OFFLINE = 0x1000;
        const uint RECALL_ON_OPEN = 0x40000;
        const uint RECALL_ON_DATA_ACCESS = 0x400000;

        if ((attrs & HIDDEN) != 0) node.Flags |= NodeFlags.Hidden;
        if ((attrs & SYSTEM) != 0) node.Flags |= NodeFlags.System;
        if ((attrs & SPARSE) != 0) node.Flags |= NodeFlags.SparseFile;
        if ((attrs & COMPRESSED) != 0) node.Flags |= NodeFlags.Compressed;

        bool isDir = (attrs & DIRECTORY) != 0;
        if (isDir)
        {
            node.Flags |= NodeFlags.Directory;
            node.AllocatedSize = 0; // totals come from children
        }

        // Reparse points: symlinks (tag 0xA*) stay as-is; cloud-filter placeholders
        // (tag family 0x9*) are not resident locally — their reported AllocationSize is
        // the remote/cloud size and MUST NOT count toward on-disk usage.
        if ((attrs & REPARSE) != 0)
        {
            node.Flags |= NodeFlags.ReparsePoint;
            uint family = reparseTag >> 24;
            if (!isDir && family == 0x90)
            {
                node.Flags |= NodeFlags.CloudPlaceholder;
                node.AllocatedSize = 0;
                return false;
            }
        }

        if (!isDir && (attrs & (OFFLINE | RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS)) != 0)
        {
            node.Flags |= NodeFlags.CloudPlaceholder;
            node.AllocatedSize = 0; // placeholder content is not resident locally
        }
        return isDir;
    }
}
