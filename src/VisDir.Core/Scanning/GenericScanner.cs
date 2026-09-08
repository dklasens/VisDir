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
    private const int InitialBufferSize = 64 * 1024; // 64 KiB (avoids LOH; grows to MaxBufferSize on demand)
    private const int MaxBufferSize = 4 << 20;       // cap per-worker scratch at 4 MiB
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
    // Per-volume extd-class enablement keyed by VolumeSerialNumber (0 = unknown).
    // One volume rejecting the class must not disable hardlink FileIds elsewhere.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _extdByVolume = new();
    private readonly ThreadLocal<byte[]?> _buffer = new(() => null);
    private readonly ThreadLocal<LocalTally> _local = new(() => new LocalTally(), trackAllValues: true);
    // Placeholder compatibility is per-process: pin once per worker thread, ignore failure.
    private readonly ThreadLocal<bool> _placeholderModeSet = new(() => false, trackAllValues: true);
    // Per-worker scratch: StringBuilder for JoinDir (avoids per-child intermediate strings)
    // and a small stack of List<FsNode>(128) so per-directory allocations reuse capacity.
    // Stack (not single slot) because HandleItem can re-enter inline when the queue is full.
    private readonly ThreadLocal<System.Text.StringBuilder> _pathBuilder =
        new(() => new System.Text.StringBuilder(260), trackAllValues: true);
    private readonly ThreadLocal<Stack<List<FsNode>>> _listPool =
        new(() => new Stack<List<FsNode>>(4), trackAllValues: true);

    // Hardlink dedup: same FileId reached via multiple directories counts allocated
    // bytes once. FileId is only available from the extended info class (key==0 otherwise).
    // Sharded by FileId hash so workers rarely contend on the same lock.
    private const int DedupShards = 16;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte>[] _seenFileIds =
        Enumerable.Range(0, DedupShards)
            .Select(_ => new System.Collections.Concurrent.ConcurrentDictionary<long, byte>())
            .ToArray();
    // FileKeys seen more than once (batch-time, FileKey only). The deterministic
    // post-join pass resolves these by (FileKey, LogicalSize) + lexicographic path.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _hardlinkDupKeys = new();
    // Reparse tag per directory node (reference key; reparse dirs are rare).
    // Lets Process skip only name-surrogate links (family 0xA0) while keeping the
    // ReparsePoint flag on all reparse points. Tag 0 = unknown (non-extd): skip.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<FsNode, uint> _dirReparseTags = new();

    private sealed class LocalTally
    {
        public long Files;
        public long Dirs;
        public long Bytes;
    }
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
        uint volSerial = (uint)volume.VolumeSerialNumber;
        channel.Writer.TryWrite(new Item(volume.RootPath, root, 0, volSerial));

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

        // Authoritative totals: sum the per-worker tallies (workers are all joined).
        // Live globals fed progress/brake checks mid-scan; the overwrite also heals
        // any race skew in those mirrors.
        long files = 0, dirs = 1, bytes = 0; // +1 root (initialized as _dirsSeen = 1)
        foreach (LocalTally t in _local.Values)
        {
            files += t.Files;
            dirs += t.Dirs;
            bytes += t.Bytes;
        }
        _filesSeen = files;
        _dirsSeen = dirs;
        _bytesSeen = bytes;
        // Deterministic hardlink attribution: keep bytes on the lexicographically-first
        // path per (FileKey, LogicalSize); zero losers so the root total stays stable
        // regardless of worker arrival order. Must run before Finalize aggregates.
        if (!_hardlinkDupKeys.IsEmpty)
            _bytesSeen -= DeterminizeHardlinks(root);
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

    private readonly record struct Item(string Path, FsNode Node, int Depth, uint VolumeSerial);

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
        _extdByVolume.Clear();
        foreach (var shard in _seenFileIds) shard.Clear();
        _hardlinkDupKeys.Clear();
        _dirReparseTags.Clear();
        foreach (LocalTally t in _local.Values) t.Files = t.Dirs = t.Bytes = 0;
        _errorCounts.Clear();
        while (_errorSamples.TryDequeue(out _)) { }
    }

    private async Task WorkerLoop(ChannelReader<Item> reader, ChannelWriter<Item> writer, CancellationToken ct)
    {
        // Pin placeholder compatibility once per worker thread so cloud files report
        // resident attributes regardless of the host process default. Best-effort.
        if (!_placeholderModeSet.Value)
        {
            try { NativeMethods.RtlSetProcessPlaceholderCompatibilityMode(NativeMethods.PHCM_DISGUISE_PLACEHOLDER); }
            catch { /* ignore: degraded placeholder view only */ }
            _placeholderModeSet.Value = true;
        }
        try
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
        finally
        {
            // Return this worker's scratch to 64 KiB at scan end so idle threads
            // don't pin a 4 MiB buffer until the next scan (or forever).
            if (_buffer.Value is { Length: > InitialBufferSize })
                _buffer.Value = new byte[InitialBufferSize];
        }
    }

    private async Task HandleItem(Item item, ChannelWriter<Item> writer, CancellationToken ct)
    {
        try
        {
            await Process(item.Path, item.Node, item.Depth, item.VolumeSerial, writer, ct).ConfigureAwait(false);
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

    private async Task Process(string dirPath, FsNode dirNode, int depth, uint volSerial, ChannelWriter<Item> writer, CancellationToken ct)
    {
        List<FsNode> children = Enumerate(dirPath, volSerial, ct);
        int childCount;
        try
        {
            // Single presize + attach (AddChildren presizes once, preserves AddChild invariants).
            dirNode.AddChildren(children);
            childCount = children.Count;

            foreach (FsNode child in children)
            {
                if ((child.Flags & NodeFlags.Directory) == 0) continue;
                // Only name-surrogate links (symlink/junction/mount-point, tag family 0xA0)
                // are never followed. Other reparse tags (e.g. cloud 0x90 dirs) are ordinary
                // directories with real children and must be descended. Tag 0 means the
                // fallback info class hid the tag — stay conservative and skip (old behavior).
                if ((child.Flags & NodeFlags.ReparsePoint) != 0)
                {
                    bool skip = true;
                    if (_dirReparseTags.TryRemove(child, out uint tag) && tag != 0)
                        skip = (tag >> 24) == 0xA0;
                    if (skip) continue;
                }
                if (depth + 1 >= MaxDepth)
                {
                    RecordError("max_depth", dirPath);
                    continue;
                }

                // Keep the queue bounded. If every worker is producing while it is full, process
                // the child inline so producers cannot deadlock waiting for themselves to read.
                Interlocked.Increment(ref _active);
                var next = new Item(JoinDirFast(dirPath, child.Name), child, depth + 1, volSerial);
                if (!writer.TryWrite(next))
                    await HandleItem(next, writer, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ReturnList(children);
        }

        if (childCount > 20_000)
            RecordError("huge_fanout", $"{dirPath} -> {childCount} entries");

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

    /// <summary>Per-worker JoinDir reusing the thread's StringBuilder (no intermediate "\\" string).</summary>
    private string JoinDirFast(string dir, string name)
    {
        var sb = _pathBuilder.Value!;
        sb.Clear();
        sb.Append(dir);
        char last = dir[dir.Length - 1];
        if (last != '\\' && last != '/') sb.Append('\\');
        sb.Append(name);
        return sb.ToString();
    }

    private List<FsNode> RentList()
    {
        var stack = _listPool.Value!;
        return stack.Count > 0 ? stack.Pop() : new List<FsNode>(128);
    }

    private void ReturnList(List<FsNode> list)
    {
        list.Clear();
        // Don't pool huge fanouts — let the large array go rather than pinning it per worker.
        if (list.Capacity > 4096) return;
        _listPool.Value!.Push(list);
    }

    /// <summary>
    /// Enumerates one directory; recovers from malformed batches by retrying THIS directory
    /// with the fallback info class. The capability downgrade (API rejects the class) is
    /// scoped per volume serial, but a shape-mismatch never poisons parsing for other directories.
    /// </summary>
    private List<FsNode> Enumerate(string dirPath, uint volSerial, CancellationToken ct)
    {
        // Hoist the long-path prefix once per directory: both the initial attempt and any
        // fallback retry share one handle path, so Extend never runs per batch or per retry.
        string extendedPath = PathUtils.Extend(dirPath);
        try
        {
            return EnumerateCore(dirPath, extendedPath, extdRequested: true, volSerial, ct);
        }
        catch (CorruptBatchException)
        {
            RecordError("extd_corrupt_retry_full", dirPath);
            try
            {
                return EnumerateCore(dirPath, extendedPath, extdRequested: false, volSerial, ct);
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
            return EnumerateCore(dirPath, extendedPath, extdRequested: false, volSerial, ct);
        }
    }

    /// <summary>Enumerates one directory, returning fully-built child nodes.</summary>
    private unsafe List<FsNode> EnumerateCore(string dirPath, string extendedPath, bool extdRequested, uint volSerial, CancellationToken ct)
    {
        IntPtr hDir = NativeMethods.CreateFileW(
            extendedPath,
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

        List<FsNode> result = RentList();
        result.Clear();
        byte[] buf = _buffer.Value ??= new byte[InitialBufferSize];
        // Capture the information class for this handle. A different worker may
        // downgrade the same volume while this directory is in flight; it
        // must not make us switch record layouts halfway through one enumeration.
        bool useExtd = extdRequested && ExtdEnabled(volSerial);
        bool owned = false;

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
                        // Scoped per volume serial (0 = unknown stays sticky for unknown only).
                        _extdByVolume[volSerial] = false;
                        throw new CapabilityDowngradeException();
                    }
                    if (err == NativeMethods.ERROR_MORE_DATA && buf.Length < MaxBufferSize)
                    {
                        // Single entry larger than the scratch: double up to the 4 MiB
                        // cap and retry on the same handle. Anything bigger is
                        // pathological — surface it instead of growing without bound.
                        buf = new byte[Math.Min(buf.Length * 2, MaxBufferSize)];
                        _buffer.Value = buf;
                        continue;
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

            }
            owned = true;
        }
        finally
        {
            NativeMethods.CloseHandle(hDir);
            // On failure the rented list stays pooled; on success ownership moves to the caller.
            if (!owned) ReturnList(result);
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
        // Once-per-batch layout probe: if the first record cannot match the assumed class,
        // fail fast so the caller restarts with the fallback layout without scanning further.
        // Per-record checks below still guard every entry; this only short-circuits the common
        // wrong-class case (extd vs full mismatch) after a single header read.
        if (cur + nameOffset + 2 > end) return 0;
        {
            uint firstNext = *(uint*)cur;
            if ((firstNext & 7) != 0) return -1;
            if (cur + offNameBytes + 4 <= end)
            {
                uint firstNameBytes = *(uint*)(cur + offNameBytes);
                if (firstNameBytes == 0 || (firstNameBytes & 1) != 0 || firstNameBytes > 65536) return -1;
            }
        }
        // The probe above validated the assumed layout for this batch: the extd path below
        // can trust field offsets and use the vectorized NUL scan.
        bool trustedExtd = extd;
        int count = 0;
        LocalTally tally = _local.Value!;
        long files0 = tally.Files, dirs0 = tally.Dirs, bytes0 = tally.Bytes;

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
            bool hasNul;
            if (trustedExtd)
            {
                // Fast path: layout already validated once per batch, so a SIMD scan suffices.
                hasNul = new ReadOnlySpan<char>(namePtr, nameLen).IndexOf('\0') >= 0;
            }
            else
            {
                hasNul = false;
                for (int ci = 0; ci < nameLen; ci++)
                {
                    if (namePtr[ci] == '\0') { hasNul = true; break; }
                }
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

            uint reparseTag = extd ? *(uint*)(cur + NativeMethods.Extd_ReparsePointTag) : 0;
            bool isDir = ApplyAttributes(node, *(uint*)(cur + offAttributes), reparseTag);
            if (isDir && (node.Flags & NodeFlags.ReparsePoint) != 0)
                _dirReparseTags[node] = reparseTag;

            // Batch-time dup detection only marks; sizing is resolved deterministically
            // post-join by lexicographic path so concurrent arrival order cannot move
            // bytes between folders. Keep the real size here; DeterminizeHardlinks zeroes losers.
            if (!isDir && fileKey != 0 &&
                !_seenFileIds[(int)((uint)fileKey.GetHashCode() & (DedupShards - 1))].TryAdd(fileKey, 0))
            {
                node.Flags |= NodeFlags.Hardlinked;
                _hardlinkDupKeys.TryAdd(fileKey, 0);
            }

            sink.Add(node);
            count++;

            // Per-entry accumulation is thread-local; one 64-bit mirror add per counter
            // per batch keeps progress/brake readers live without per-file contention.
            if (isDir)
                tally.Dirs++;
            else
            {
                tally.Files++;
                tally.Bytes += (long)node.AllocatedSize;
            }

            if (next == 0) break; // 0 marks the last entry in this batch
            cur += next;
        }
        // Commit this batch's deltas once, so progress/brake readers stay live with
        // O(1) atomics per batch instead of per entry. Final totals come from the
        // join-time tally sum in Scan().
        long dirs = Interlocked.Add(ref _dirsSeen, tally.Dirs - dirs0);
        Interlocked.Add(ref _filesSeen, tally.Files - files0);
        Interlocked.Add(ref _bytesSeen, tally.Bytes - bytes0);
        if (dirs > MaxDirsSafetyLimit)
        {
            _braked = true;
            _cancelled = true;
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

        // Reparse points keep the flag regardless of tag; traversal is gated in Process
        // (only name-surrogate family 0xA0 is skipped). Dehydrated placeholders are
        // sparse and already report AllocationSize == 0, so no tag-family zeroing here:
        // only OFFLINE|RECALL_ON_* below zeroes + marks CloudPlaceholder.
        if ((attrs & REPARSE) != 0)
            node.Flags |= NodeFlags.ReparsePoint;

        if (!isDir && (attrs & (OFFLINE | RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS)) != 0)
        {
            node.Flags |= NodeFlags.CloudPlaceholder;
            node.AllocatedSize = 0; // placeholder content is not resident locally
        }
        return isDir;
    }

    private bool ExtdEnabled(uint volSerial) =>
        !_extdByVolume.TryGetValue(volSerial, out bool enabled) || enabled;

    /// <summary>
    /// Deterministic post-join hardlink resolution: groups file nodes by
    /// (FileKey, LogicalSize) for keys known to collide, keeps AllocatedSize only on
    /// the lexicographically-first full path, zeroes losers. Returns total bytes zeroed
    /// so Scan can keep _bytesSeen consistent with the tree. Single-threaded (post-join).
    /// LogicalSize must match to guard low-64 collisions on ReFS/SMB (FILE_ID_128 truncation).
    /// </summary>
    private long DeterminizeHardlinks(FsNode root)
    {
        var groups = new Dictionary<(long Key, ulong Size), List<FsNode>>();
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            FsNode n = stack.Pop();
            var children = n.Children;
            if (children is null) continue;
            foreach (FsNode child in children)
            {
                if ((child.Flags & NodeFlags.Directory) != 0)
                {
                    stack.Push(child);
                    continue;
                }
                if (child.FileKey == 0) continue;
                if (!_hardlinkDupKeys.ContainsKey(child.FileKey)) continue;
                var k = (child.FileKey, child.LogicalSize);
                if (!groups.TryGetValue(k, out List<FsNode>? list))
                    groups[k] = list = new List<FsNode>(2);
                list.Add(child);
            }
        }

        long zeroed = 0;
        foreach (List<FsNode> members in groups.Values)
        {
            if (members.Count == 1)
            {
                // Spurious batch-time mark: same low-64 FileKey but no size match.
                // Not a hardlink — clear the flag, keep the bytes.
                members[0].Flags &= ~NodeFlags.Hardlinked;
                continue;
            }
            FsNode? best = null;
            string? bestPath = null;
            foreach (FsNode m in members)
            {
                string p = m.GetPath();
                if (best is null || string.CompareOrdinal(p, bestPath) < 0)
                {
                    best = m;
                    bestPath = p;
                }
            }
            foreach (FsNode m in members)
            {
                if (ReferenceEquals(m, best))
                    m.Flags &= ~NodeFlags.Hardlinked;
                else
                {
                    m.Flags |= NodeFlags.Hardlinked;
                    zeroed += (long)m.AllocatedSize;
                    m.AllocatedSize = 0;
                }
            }
        }
        return zeroed;
    }
}
