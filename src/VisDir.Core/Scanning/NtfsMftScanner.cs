using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VisDir.Core.Interop;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VisDir.Core.Tests")]
namespace VisDir.Core.Scanning;

/// <summary>Raised when the MFT path is unavailable because the process is not elevated.</summary>
public sealed class AdminRequiredException : Exception
{
    public AdminRequiredException() : base("Administrator privileges are required for raw MFT scanning.") { }
}

/// <summary>
/// WizTree-class fast path: reads the NTFS Master File Table sequentially and rebuilds the
/// whole volume tree from raw records, then prunes the result to the requested path.
/// Requires elevation; NTFS volumes only.
/// </summary>
public sealed class NtfsMftScanner : IDiskScanner
{
    private const int ReadBufferSize = 32 << 20; // 32 MiB sequential chunks
    private const int RootRecordNumber = 5;
    private const int BadClusRecordNumber = 8;
    private const int SystemRecordCount = 16;
    private const int ProgressRecordStride = 50_000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint ReparseTagMountPoint = 0xA0000003; // IO_REPARSE_TAG_MOUNT_POINT
    private const uint ReparseTagSymlink = 0xA000000C; // IO_REPARSE_TAG_SYMLINK

    // $REPARSE_POINT tag (MftEntryInfo.ReparseTag, 0 when absent). Only
    // mount points and symlinks block descent; cloud placeholders (0x90 family)
    // are ordinary directories with real children.
    private static uint GetReparseTag(long recNo, Dictionary<long, MftEntryInfo> entries) =>
        entries.TryGetValue(recNo, out MftEntryInfo e) ? e.ReparseTag : 0;

    private static bool IsMountOrSymlinkTarget(long parentRec, Dictionary<long, MftEntryInfo> entries)
    {
        uint tag = GetReparseTag(parentRec, entries);
        return tag == ReparseTagMountPoint || tag == ReparseTagSymlink;
    }

    // Folds one extension record's reparse tag into its base (keeps a non-zero base tag).
    private static void MergeReparseTag(ref MftEntryInfo baseInfo, in MftEntryInfo ext)
    {
        if (baseInfo.ReparseTag != 0) return;
        if (ext.ReparseTag == 0) return;
        baseInfo.ReparseTag = ext.ReparseTag;
    }

    // Validate each directory once, including cycles longer than the former 64-hop cap.
    private static HashSet<long> FindParentCycles(Dictionary<long, MftEntryInfo> entries, CancellationToken ct)
    {
        var state = new Dictionary<long, byte>();
        var cycles = new HashSet<long>();
        var trail = new List<long>();
        foreach ((long start, MftEntryInfo entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsDirectory || state.ContainsKey(start)) continue;
            trail.Clear();
            long current = start;
            while (current != RootRecordNumber && entries.TryGetValue(current, out MftEntryInfo info) && info.IsDirectory)
            {
                if (state.TryGetValue(current, out byte mark))
                {
                    if (mark == 1)
                    {
                        int beginning = trail.IndexOf(current);
                        for (int i = beginning; i < trail.Count; i++) cycles.Add(trail[i]);
                    }
                    break;
                }
                state[current] = 1;
                trail.Add(current);
                long parent = (long)info.ParentRecordNumber;
                if (parent == current) break;
                current = parent;
            }
            foreach (long item in trail) state[item] = 2;
        }
        return cycles;
    }
    public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
    {
        TokenPrivilegeManager.TryEnableBackupPrivileges();
        string requestedPath = PathUtils.NormalizeScanRoot(options.Path);
        VolumeInfo capacity = VolumeQuery.Query(requestedPath);
        string volumeDevice = GetVolumeDevice(requestedPath);

        IntPtr hVolume = NativeMethods.CreateFileW(
            volumeDevice, NtfsNative.GenericRead, NtfsNative.ShareReadWriteDelete,
            IntPtr.Zero, NtfsNative.OpenExisting, 0, IntPtr.Zero);

        if (hVolume == (IntPtr)(-1))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == NativeMethods.ERROR_ACCESS_DENIED) throw new AdminRequiredException();
            throw new Win32Exception(err);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            if (!NtfsNative.DeviceIoControl(hVolume, NtfsNative.FsctlGetNtfsVolumeData,
                    IntPtr.Zero, 0, out NtfsNative.NtfsVolumeDataBuffer vol,
                    (uint)Marshal.SizeOf<NtfsNative.NtfsVolumeDataBuffer>(),
                    out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_NTFS_VOLUME_DATA failed (not NTFS?)");

            cancellationToken.ThrowIfCancellationRequested();

            ValidateVolumeIdentity(capacity.VolumeSerialNumber, vol.VolumeSerialNumber);
            capacity = capacity with { BytesPerCluster = vol.BytesPerCluster, VolumeSerialNumber = vol.VolumeSerialNumber, FileSystemName = "NTFS" };
            var phases = new Dictionary<string, double>();
            var phaseClock = Stopwatch.StartNew();
            Dictionary<long, MftEntryInfo> entries = ReadMft(hVolume, volumeDevice, in vol, cancellationToken, progress);
            phases["ReadAndParse"] = phaseClock.Elapsed.TotalMilliseconds;
            phaseClock.Restart();
            progress?.Report(new ScanProgress(entries.Count, 0, 0, sw.Elapsed.TotalMilliseconds, 0.98, "Building directory tree"));
            FsNode root = BuildTree(entries, capacity, cancellationToken);

            // The MFT read is always volume-wide; narrow the returned tree to the requested
            // subpath so callers see the same root contract as the compatible scanner.
            FsNode pruned = PruneToRequestedPath(root, requestedPath);
            phases["Build"] = phaseClock.Elapsed.TotalMilliseconds;
            phaseClock.Restart();
            HardlinkAccounting.Apply(pruned, cancellationToken, onlyMarkedHardlinks: true);
            TreeOps.Finalize(pruned, cancellationToken);
            phases["Finalize"] = phaseClock.Elapsed.TotalMilliseconds;
            (long files, long dirs, bool orphaned) = CountSubtree(pruned, cancellationToken);

            return new ScanResult
            {
                Volume = capacity,
                Root = pruned,
                EngineName = "mft",
                Completeness = orphaned ? ScanCompleteness.Partial : ScanCompleteness.Complete,
                Issues = orphaned
                    ? new[] { new ScanIssue(requestedPath, "Some MFT records could not be placed in their original folders and are shown under [orphaned].") }
                    : Array.Empty<ScanIssue>(),
                AccountingFeatures = AccountingFeatures.UnnamedData | AccountingFeatures.NamedStreams
                    | AccountingFeatures.DirectoryMetadata | AccountingFeatures.FileIdentity,
                Stats = new ScanStats
                {
                    FileCount = files,
                    DirectoryCount = dirs,
                    ErrorCount = orphaned ? 1 : 0,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    BytesSeen = pruned.TotalAllocated,
                    PhaseMs = phases,
                },
            };
        }
        finally
        {
            NativeMethods.CloseHandle(hVolume);
        }
    }

    internal static void ValidateVolumeIdentity(ulong requestedSerial, ulong rawSerial)
    {
        if (requestedSerial != 0 && requestedSerial != rawSerial)
            throw new IOException("The requested folder belongs to a mounted volume; use the compatible scanner for this path.");
    }

    /// <summary>
    /// Narrows a volume-wide MFT tree to <paramref name="requestedPath"/> (already normalized).
    /// A drive root returns the whole tree; a subpath walks down by name segments, detaches the
    /// match, and renames it to the full requested path so the root contract matches the
    /// compatible scanner. Throws when the subpath is absent from the volume tree.
    /// </summary>
    private static FsNode PruneToRequestedPath(FsNode volumeRoot, string requestedPath)
    {
        string volumePrefix = $"{char.ToUpperInvariant(requestedPath[0])}:{Path.DirectorySeparatorChar}";
        if (requestedPath.Equals(volumePrefix, StringComparison.OrdinalIgnoreCase))
            return volumeRoot;
        string relative = requestedPath.Length > 3
            ? requestedPath[3..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : string.Empty;
        FsNode current = volumeRoot;
        foreach (string segment in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            FsNode? next = current.Children?.FirstOrDefault(c =>
                c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                throw new InvalidOperationException(
                    $"MFT scanning reads the whole volume; '{requestedPath}' was not found in the volume tree. " +
                    "Use the compatible scanner for paths outside this volume.");
            current = next;
        }
        current.Parent = null;
        current.Name = requestedPath;
        return current;
    }

    /// <summary>Single-pass file/directory count over a (possibly pruned) subtree.</summary>
    private static (long Files, long Dirs, bool Orphaned) CountSubtree(FsNode root, CancellationToken ct)
    {
        long files = 0, dirs = 0;
        bool orphaned = false;
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            FsNode node = stack.Pop();
            orphaned |= (node.Flags & NodeFlags.OrphanedRoot) != 0;
            if (node.IsDirectory) dirs++;
            else files++;
            if (node.Children is { } kids)
                foreach (FsNode kid in kids)
                    stack.Push(kid);
        }
        return (files, dirs, orphaned);
    }

    public static string GetVolumeDevice(string normalizedRoot)
    {
        char drive = char.ToUpperInvariant(normalizedRoot[0]);
        if (normalizedRoot.Length < 2 || normalizedRoot[1] != ':' || drive < 'A' || drive > 'Z')
            throw new ArgumentException("MFT scanning requires a local drive letter root.", nameof(normalizedRoot));
        return $"\\\\.\\{drive}:";
    }

    private static unsafe Dictionary<long, MftEntryInfo> ReadMft(
        IntPtr hVolume, string volumeDevice, in NtfsNative.NtfsVolumeDataBuffer vol,
        CancellationToken ct, IProgress<ScanProgress>? progress)
    {
        uint recordSize = vol.BytesPerFileRecordSegment;
        if (recordSize is < 512 or > 4096 || !int.IsPow2((int)recordSize))
            throw new IOException($"Unexpected MFT record size {recordSize}.");

        ulong validLength = vol.MftValidDataLength;
        if (vol.BytesPerCluster == 0 || validLength == 0 || validLength % recordSize != 0
            || validLength > checked(vol.TotalClusters * vol.BytesPerCluster))
            throw new IOException("Invalid MFT valid-data length.");

        // Size for the estimated live-record count so small volumes don't pay 1M slots
        // up front; sparse/deleted-heavy MFTs still grow naturally from here.
        var entries = new Dictionary<long, MftEntryInfo>(
            capacity: (int)Math.Clamp(validLength / recordSize, 1024, 1 << 20));
        var extensionRecNos = new List<long>(1024);
        var buffer = GC.AllocateUninitializedArray<byte>(ReadBufferSize);
        bool trace = Environment.GetEnvironmentVariable("VISDIR_TRACE_ERRORS") == "1";
        long nSlots = 0, nMagic = 0, nFixup = 0, nStruct = 0, nNotInUse = 0, nDup = 0;

        long recordsSeen = 0;
        ulong bytesProcessed = 0;
        var progressClock = Stopwatch.StartNew();
        long lastReportMs = -250; // first chunk reports immediately, then >=250ms apart
        // Tier 1: open $MFT through the filesystem namespace — the kernel resolves
        // fragmentation/attribute-lists for us, so plain sequential reads suffice.
        // Backup semantics lets admin/backup-privileged callers past its restrictive ACL.
        // OVERLAPPED allows the Tier-1 double-buffer below (chunk N+1 reads while N parses).
        // Tier 2 resolves record zero and its attribute list through raw volume reads.
        IntPtr hMft = NativeMethods.CreateFileW(
            $"{volumeDevice}\\$MFT",
            NtfsNative.GenericRead, NtfsNative.ShareReadWriteDelete,
            IntPtr.Zero, NtfsNative.OpenExisting,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NtfsNative.FileFlagOverlapped | FileFlagSequentialScan, IntPtr.Zero);

        bool viaMftHandle = hMft != (IntPtr)(-1);
        if (!viaMftHandle)
        {
            int openErr = Marshal.GetLastWin32Error(); // capture before anything can clobber it
            if (trace) Console.Error.WriteLine($"MFTOPEN failed err={openErr} — using volume extents");
        }

        try
        {
            fixed (byte* bufPtr = buffer)
            {
                void ParseBuffer(byte* basePtr, uint bytes, ulong baseOff)
                {
                    ulong baseRecNo = baseOff / recordSize;
                    uint usable = bytes - bytes % recordSize;
                    for (uint off = 0; off + recordSize <= usable; off += recordSize)
                    {
                        if ((off & 0x003fffff) == 0) ct.ThrowIfCancellationRequested();
                        byte* rec = basePtr + off;
                        nSlots++;
                        if (*rec != (byte)'F')
                        {
                            if ((*(ushort*)(rec + 0x16) & 1) != 0)
                                throw new IOException($"Invalid in-use MFT signature at record {baseRecNo + off / recordSize}.");
                            continue;
                        }
                        nMagic++;
                        // Cheap pre-parse: the in-use flag sits at a fixed offset, so skip
                        // dead slots without walking attributes (post-parse check stays).
                        if (recordSize >= 0x18 && (*(ushort*)(rec + 0x16) & 1) == 0) { nNotInUse++; continue; }

                        if (!NtfsRecordParser.TryParseRecord(rec, (int)recordSize, out MftEntryInfo info, out byte stage)
                            || info.Incomplete)
                        {
                            if (stage == NtfsRecordParser.FailFixup) nFixup++;
                            else if (stage == NtfsRecordParser.FailStructure) nStruct++;
                            throw new IOException($"Incomplete in-use MFT record {baseRecNo + off / recordSize} (parse stage {stage}).");
                        }
                        if (!info.InUse) { nNotInUse++; continue; }

                        // Trust the read offset over the body: the body number can be stale
                        // on reused slots. Body stays as fallback when out of range.
                        ulong derived = baseRecNo + off / recordSize;
                        if (derived <= (ulong)long.MaxValue) info.RecordNumber = (long)derived;

                        // $BadClus's $DATA intentionally spans the whole volume, but only
                        // when it actually claims sparse/compressed allocation.
                        if (info.RecordNumber == BadClusRecordNumber && (info.Sparse || info.Compressed))
                        {
                            info.LogicalSize = info.DataAllocatedSize = info.AdsAllocatedSize = 0;
                        }

                        // Offsets are parsed once, so overwrites should be ~zero; count them
                        // so silent record replacement stays visible in MFTSTAT. Value wins,
                        // exactly as before — zero behavior change.
                        if (!entries.TryAdd(info.RecordNumber, info)) { entries[info.RecordNumber] = info; nDup++; }
                        if ((info.IsExtension || info.BaseRecordNumber != 0) && info.BaseRecordNumber != info.RecordNumber)
                        {
                            extensionRecNos.Add(info.RecordNumber);
                        }
                    }

                    recordsSeen += usable / recordSize;
                    if (recordsSeen >= ProgressRecordStride) recordsSeen %= ProgressRecordStride;
                }

                void ReportProgress(uint bytes, bool flush = false)
                {
                    bytesProcessed += bytes;
                    long now = progressClock.ElapsedMilliseconds;
                    if (!flush && now - lastReportMs < 250) return;
                    lastReportMs = now;
                    double fraction = validLength > 0 ? Math.Min(0.98, (double)bytesProcessed / validLength) : -1;
                    progress?.Report(new ScanProgress(
                        entries.Count, 0, bytesProcessed, progressClock.Elapsed.TotalMilliseconds,
                        fraction, "Reading NTFS file table"));
                }

                ulong remaining = validLength;

                if (viaMftHandle)
                {
                    // RandomAccess owns the OVERLAPPED lifetime, including cancellation. Each
                    // request has an explicit offset; the one-buffer path uses the same API.
                    using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(hMft, ownsHandle: false);
                    using var reads = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    byte[]? nextBuffer = null;
                    try { nextBuffer = GC.AllocateUninitializedArray<byte>(ReadBufferSize); }
                    catch (OutOfMemoryException) { /* one buffer still scans correctly */ }
                    byte[] current = buffer;
                    ulong offset = 0;
                    Task<int>? pending = null;
                    try
                    {
                        while (remaining > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            int want = (int)Math.Min(remaining, (uint)current.Length);
                            int got = pending is not null
                                ? pending.GetAwaiter().GetResult()
                                : RandomAccess.ReadAsync(handle, current.AsMemory(0, want), checked((long)offset), reads.Token)
                                    .AsTask().GetAwaiter().GetResult();
                            pending = null;
                            if (got != want)
                                throw new IOException($"Incomplete MFT read at {offset}: expected {want}, received {got}.");
                            ulong nextOffset = offset + (uint)got;
                            ulong nextRemaining = remaining - (uint)got;
                            if (nextBuffer is not null && nextRemaining > 0)
                            {
                                int nextWant = (int)Math.Min(nextRemaining, (uint)nextBuffer.Length);
                                pending = RandomAccess.ReadAsync(handle, nextBuffer.AsMemory(0, nextWant),
                                    checked((long)nextOffset), reads.Token).AsTask();
                            }
                            fixed (byte* currentPtr = current) ParseBuffer(currentPtr, (uint)got, offset);
                            ReportProgress((uint)got);
                            remaining = nextRemaining;
                            offset = nextOffset;
                            if (nextBuffer is not null) (current, nextBuffer) = (nextBuffer, current);
                        }
                    }
                    finally
                    {
                        // Do not close the handle or release a buffer while its next read is pending.
                        reads.Cancel();
                        if (pending is not null)
                        {
                            try { pending.GetAwaiter().GetResult(); }
                            catch (OperationCanceledException) { }
                            catch (IOException) { }
                        }
                    }
                }
                else
                {
                    List<(long StartLcn, ulong Clusters)> extents =
                        DiscoverMftExtents(hVolume, in vol, validLength, ct);

                    ulong mftBase = 0; // MFT-logical offset across extents (volume pos differs)
                    foreach ((long lcn, ulong clusters) in extents)
                    {
                        if (remaining == 0) break;
                        long pos = checked(lcn * (long)vol.BytesPerCluster);
                        long extentBytes = checked((long)Math.Min(clusters * vol.BytesPerCluster, remaining));

                        while (extentBytes > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            uint want = (uint)Math.Min(extentBytes, (uint)buffer.Length);
                            if (!NtfsNative.SetFilePointerEx(hVolume, pos, out _, 0))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            // `want` is clamped to `extentBytes`, so a failed/zero read with
                            // bytes left is an anomaly, never end-of-extent (normal end exits
                            // via `extentBytes > 0`). Fail closed like the Tier-1 resume.
                            if (!NtfsNative.ReadFile(hVolume, bufPtr, want, out uint got, IntPtr.Zero) || got != want)
                            {
                                throw new IOException(
                                    $"Short MFT extent read at volume offset {pos}: wanted {want}, got {got} (remaining {remaining}).");
                            }

                            ParseBuffer(bufPtr, got, mftBase);
                            mftBase += got;
                            ReportProgress(got);
                            pos += got;
                            extentBytes -= got;
                            remaining -= Math.Min(remaining, got);
                        }
                    }
                }

                if (remaining != 0) throw new IOException($"Incomplete MFT scan: {remaining} bytes were not read.");
                ReportProgress(0, flush: true); // final flush so the bar never sticks
            }
        }
        finally
        {
            if (viaMftHandle) NativeMethods.CloseHandle(hMft);
        }

        if (trace)
        {
            Console.Error.WriteLine(
                $"MFTSTAT slots={nSlots} magic={nMagic} fixupFail={nFixup} structFail={nStruct} " +
                $"notInUse={nNotInUse} dup={nDup} kept={entries.Count}");
        }

        MergeExtensionRecords(entries, extensionRecNos, ct);
        ZeroBadClus(entries);
        return entries;
    }

    // Rec #8 is NTFS-reserved metadata; its $Bad stream nominally spans the volume
    // (holes), so billing it double-counts unallocated space. The named branch never
    // sets Sparse, so flag-gated guards cannot cover it. Post-fold placement defeats
    // Ads-SUM resurrection from spill segments.
    internal static void ZeroBadClus(Dictionary<long, MftEntryInfo> entries)
    {
        if (entries.TryGetValue(BadClusRecordNumber, out MftEntryInfo bad))
        {
            bad.LogicalSize = 0;
            bad.DataAllocatedSize = 0;
            bad.AdsAllocatedSize = 0;
            bad.IndexAllocationSize = 0;
            entries[BadClusRecordNumber] = bad;
        }
    }

    /// <summary>
    /// Records with a non-zero base reference are $ATTRIBUTE_LIST extensions of their base
    /// record (huge/fragmented files spill $DATA instances there, without a $FILE_NAME).
    /// Fold their sizes into the base so tree assembly sees one complete node per file.
    /// </summary>
    internal static void MergeExtensionRecords(Dictionary<long, MftEntryInfo> entries, List<long> extensionRecNos, CancellationToken ct = default)
    {
        if (extensionRecNos.Count == 0) return;
        var removed = new HashSet<long>(extensionRecNos.Count);
        List<long>? deferred = null; // base is itself an extension: resolve after bases fold

        static void Fold(ref MftEntryInfo baseInfo, in MftEntryInfo ext)
        {
            baseInfo.LogicalSize = Math.Max(baseInfo.LogicalSize, ext.LogicalSize);
            // The lowest-VCN-zero record contains the complete primary-stream
            // allocation. Never add continuation records to it.
            baseInfo.DataAllocatedSize = Math.Max(baseInfo.DataAllocatedSize, ext.DataAllocatedSize);
            baseInfo.AdsAllocatedSize = TreeOps.SaturatingAdd(baseInfo.AdsAllocatedSize, ext.AdsAllocatedSize);
            baseInfo.IndexAllocationSize = TreeOps.SaturatingAdd(baseInfo.IndexAllocationSize, ext.IndexAllocationSize);
            baseInfo.Compressed |= ext.Compressed;
            baseInfo.Sparse |= ext.Sparse;
            baseInfo.HasPrimaryData |= ext.HasPrimaryData;
            baseInfo.Reparse |= ext.Reparse;
            baseInfo.Offline |= ext.Offline;
            baseInfo.Incomplete |= ext.Incomplete;
            if ((!baseInfo.HasFileName || baseInfo.NameRank <= 1) && ext.NameLinks is not null)
                baseInfo.NameLinks = new List<MftNameInfo>(ext.NameLinks);
            else if (ext.NameLinks is { } links)
                foreach (MftNameInfo link in links) NtfsRecordParser.AddNameLink(ref baseInfo, link);
            else if (ext.HasFileName && ext.NameRank > 1)
                NtfsRecordParser.AddNameLink(ref baseInfo, new MftNameInfo(ext.ParentRecordNumber, ext.Name));
            // Adopt the better-ranked name across the fold boundary: a base holding
            // only a DOS-mangled name (seen live: base carries DOS-only $FILE_NAME
            // while WIN32 lives in an extension record) loses to a WIN32 extension.
            if (ext.HasFileName && ext.NameRank > baseInfo.NameRank)
            {
                baseInfo.Name = ext.Name;
                baseInfo.HasFileName = true;
                baseInfo.NameRank = ext.NameRank;
                baseInfo.ParentRecordNumber = ext.ParentRecordNumber;
            }
            baseInfo.FileNameLinks += ext.FileNameLinks;
            baseInfo.IsDirectory |= ext.IsDirectory;
            MergeReparseTag(ref baseInfo, in ext);
        }

        foreach (long recNo in extensionRecNos)
        {
            ct.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(recNo, out MftEntryInfo ext)) continue;
            if ((!ext.IsExtension && ext.BaseRecordNumber == 0) || ext.BaseRecordNumber == recNo) continue;
            if (!entries.TryGetValue(ext.BaseRecordNumber, out MftEntryInfo baseInfo)) continue;
            if (removed.Contains(ext.BaseRecordNumber)
                || (baseInfo.BaseRecordNumber != 0 && baseInfo.BaseRecordNumber != ext.BaseRecordNumber))
            {
                (deferred ??= new List<long>(extensionRecNos.Count)).Add(recNo);
                continue;
            }

            Fold(ref baseInfo, in ext);
            entries[ext.BaseRecordNumber] = baseInfo;
            removed.Add(recNo);
        }

        // One final sweep: follow each deferred chain to its ultimate base (cycle-guarded)
        // so ext -> ext -> base folds without re-scanning the whole list.
        if (deferred is not null)
        {
            foreach (long recNo in deferred)
            {
                ct.ThrowIfCancellationRequested();
                if (removed.Contains(recNo)) continue;
                if (!entries.TryGetValue(recNo, out MftEntryInfo ext)) continue;
                long ultimate = ext.BaseRecordNumber;
                if (ultimate == 0 || ultimate == recNo) continue;
                for (int hop = 0; hop < 8; hop++)
                {
                    if (!entries.TryGetValue(ultimate, out MftEntryInfo cur)) break;
                    if (cur.BaseRecordNumber == 0 || cur.BaseRecordNumber == ultimate) break;
                    ultimate = cur.BaseRecordNumber;
                }
                if (ultimate == recNo || removed.Contains(ultimate)) continue;
                if (!entries.TryGetValue(ultimate, out MftEntryInfo baseInfo)) continue;
                Fold(ref baseInfo, in ext);
                entries[ultimate] = baseInfo;
                removed.Add(recNo);
            }
        }

        foreach (long recNo in removed) entries.Remove(recNo);
    }

    private static unsafe List<(long StartLcn, ulong Clusters)> DiscoverMftExtents(
        IntPtr hVolume, in NtfsNative.NtfsVolumeDataBuffer vol, ulong validLength, CancellationToken ct)
    {
        uint sectorSize = Math.Max(vol.BytesPerSector, 512);
        byte[] ReadPhysical(ulong offset, int length)
        {
            ct.ThrowIfCancellationRequested();
            ulong alignedOffset = offset / sectorSize * sectorSize;
            int prefix = checked((int)(offset - alignedOffset));
            int alignedLength = checked((int)(((ulong)prefix + (uint)length + sectorSize - 1) / sectorSize * sectorSize));
            byte[] bytes = GC.AllocateUninitializedArray<byte>(alignedLength);
            fixed (byte* pointer = bytes)
            {
                if (!NtfsNative.SetFilePointerEx(hVolume, checked((long)alignedOffset), out _, 0)
                    || !NtfsNative.ReadFile(hVolume, pointer, (uint)alignedLength, out uint got, IntPtr.Zero)
                    || got != alignedLength)
                    throw new IOException("Failed to read complete MFT extent metadata.");
            }
            return prefix == 0 && length == alignedLength ? bytes : bytes.AsSpan(prefix, length).ToArray();
        }
        return MftExtentReader.Discover(ReadPhysical, vol.BytesPerCluster,
            (int)vol.BytesPerFileRecordSegment, vol.MftStartLcn, vol.TotalClusters, validLength, ct);
    }
    private static readonly string[] SystemNames =
    {
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "", "$Bitmap", "$Boot",
        "$BadClus", "$Secure", "$UpCase", "$Extend", "$Reserved12", "$Reserved13",
        "$Reserved14", "$Reserved15",
    };

    internal static FsNode BuildTree(Dictionary<long, MftEntryInfo> entries, VolumeInfo capacity, CancellationToken ct = default)
    {
        if (!entries.TryGetValue(RootRecordNumber, out MftEntryInfo rootEntry) || !rootEntry.IsDirectory)
            throw new IOException("The MFT does not contain a valid root directory.");
        HashSet<long> cycles = FindParentCycles(entries, ct);
        FsNode root = BuildTreeDense(entries, capacity, cycles, ct);
        var directories = new Dictionary<ulong, FsNode>();
        var stack = new Stack<FsNode>();
        stack.Push(root);
        FsNode? orphans = null;
        while (stack.TryPop(out FsNode? node))
        {
            ct.ThrowIfCancellationRequested();
            if ((node.Flags & NodeFlags.OrphanedRoot) != 0) orphans = node;
            else if (node.IsDirectory) directories[(ulong)node.FileKey & 0x0000FFFFFFFFFFFFUL] = node;
            if (node.Children is { } children)
                foreach (FsNode child in children) if (child.IsDirectory) stack.Push(child);
        }
        foreach ((long recNo, MftEntryInfo entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.NameLinks is not { Count: > 1 } links) continue;
            foreach (MftNameInfo link in links)
            {
                if (link.ParentRecordNumber == entry.ParentRecordNumber && link.Name == entry.Name) continue;
                if (IsMountOrSymlinkTarget((long)link.ParentRecordNumber, entries)) continue;
                if (!directories.TryGetValue(link.ParentRecordNumber, out FsNode? parent))
                {
                    if (orphans is null)
                    {
                        orphans = new FsNode { Name = "[orphaned]", Flags = NodeFlags.Directory | NodeFlags.OrphanedRoot };
                        root.AddChild(orphans);
                    }
                    parent = orphans;
                }
                var alias = new FsNode
                {
                    Name = link.Name,
                    FileKey = unchecked((long)(((ulong)entry.SequenceNumber << 48) | (ulong)recNo)),
                    VolumeKey = capacity.VolumeSerialNumber,
                };
                ApplyInfo(alias, entry, capacity.BytesPerCluster);
                alias.Flags |= NodeFlags.Hardlinked;
                parent.AddChild(alias);
            }
        }
        return root;
    }

    private static FsNode BuildTreeDense(Dictionary<long, MftEntryInfo> entries, VolumeInfo capacity, HashSet<long> cycles, CancellationToken ct)
    {
        // Single materialization: node refs live in one pooled record-keyed array instead of a
        // second Dictionary<long,FsNode>. Peak is entries-dict + array (8 B/slot, pooled),
        // not dict+dict. The array is returned after linking; the tree keeps refs via Parent/Children.
        long maxRec = 0;
        foreach (long k in entries.Keys)
            if (k > maxRec) maxRec = k;
        if (maxRec < 0 || maxRec > int.MaxValue - 1 || maxRec > (long)entries.Count * 8L + 4096 || maxRec > 32_000_000L)
            return BuildTreeSparse(entries, capacity, cycles, ct);

        int size = (int)maxRec + 1;
        FsNode?[] slots = ArrayPool<FsNode?>.Shared.Rent(size);
        Array.Clear(slots, 0, size);
        int[] counts = ArrayPool<int>.Shared.Rent(size);
        Array.Clear(counts, 0, size);
        try
        {
            foreach ((long recNo, MftEntryInfo e) in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (recNo < 0 || recNo >= size) continue; // defensive: rec nos are dense uints
                bool isSystem = recNo < SystemRecordCount;
                string name = e.HasFileName && e.Name.Length > 0 ? e.Name
                    : isSystem && SystemNames[(int)recNo].Length > 0 ? SystemNames[(int)recNo]
                    : $"#{recNo}";
                var node = new FsNode
                {
                    Name = name,
                    FileKey = unchecked((long)(((ulong)e.SequenceNumber << 48) | (ulong)recNo)),
                    VolumeKey = capacity.VolumeSerialNumber,
                };
                ApplyInfo(node, e, capacity.BytesPerCluster);
                slots[(int)recNo] = node;
            }

            FsNode root = (RootRecordNumber < size ? slots[RootRecordNumber] : null)
                ?? new FsNode { Name = "(root)", FileKey = RootRecordNumber };
            root.Name = capacity.RootPath;

            var orphans = new FsNode
            {
                Name = "[orphaned]",
                Flags = NodeFlags.Directory | NodeFlags.OrphanedRoot,
            };

            static FsNode? ResolveTarget(
                long recNo, FsNode?[] slots, int size,
                Dictionary<long, MftEntryInfo> entries, FsNode root, FsNode orphans)
            {
                if (recNo == RootRecordNumber) return null;
                if (!entries.TryGetValue(recNo, out MftEntryInfo e)) return null;
                long parentRec = unchecked((long)e.ParentRecordNumber);
                if (parentRec != recNo && parentRec >= 0 && parentRec < size && slots[(int)parentRec] is { } parent)
                {
                    // A non-directory parent cannot contain children: corrupt parent refs
                    // attach system records to the root and route the rest to orphans
                    // instead of linking files under a file.
                    if (entries.TryGetValue(parentRec, out MftEntryInfo parentInfo) && !parentInfo.IsDirectory)
                        return recNo < SystemRecordCount ? root : orphans;
                    // Only mount points and symlinks stay detached (GenericScanner doesn't
                    // enumerate inside them, so attaching here would double-count). Other
                    // reparse dirs — incl. cloud placeholders — attach normally. System
                    // entries detached here are re-attached to the root below.
                    if ((parent.Flags & (NodeFlags.Directory | NodeFlags.ReparsePoint))
                        == (NodeFlags.Directory | NodeFlags.ReparsePoint)
                        && IsMountOrSymlinkTarget(parentRec, entries))
                        return recNo < SystemRecordCount ? root : null;
                    return parent;
                }
                else if (parentRec == recNo)
                {
                    // Self-referential (system files do this): legacy first loop leaves system
                    // self-refs detached and the second loop moves them to the root; non-system
                    // self-refs attach directly. Final target is the root either way.
                    return root;
                }
                else if (recNo >= SystemRecordCount)
                {
                    return orphans;
                }
                else if (parentRec != 0)
                {
                    return root; // missing parent record (deleted dir etc.)
                }
                else
                {
                    return orphans;
                }
            }

            int rootCount = 0, orphanCount = 0;
            foreach (long recNo in entries.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (recNo == RootRecordNumber || recNo < 0 || recNo >= size) continue;
                FsNode? target = ResolveTarget(recNo, slots, size, entries, root, orphans);
                if (target is null) continue;
                else if (ReferenceEquals(target, root)) rootCount++;
                else if (ReferenceEquals(target, orphans)) orphanCount++;
                else
                {
                    long parentRec = unchecked((long)entries[recNo].ParentRecordNumber);
                    counts[(int)parentRec]++;
                }
            }
            for (int i = 0; i < size; i++)
                if (counts[i] > 0 && slots[i] is { } n) n.EnsureCapacity(counts[i]);
            root.EnsureCapacity(rootCount + (orphanCount > 0 ? 1 : 0));
            if (orphanCount > 0) orphans.EnsureCapacity(orphanCount);

            foreach (long recNo in entries.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (recNo == RootRecordNumber || recNo < 0 || recNo >= size) continue;
                FsNode? node = slots[(int)recNo];
                if (node is null || node.Parent is not null) continue;
                FsNode? target = ResolveTarget(recNo, slots, size, entries, root, orphans);
                if (target is null) continue;
                // Corrupt parent cycles would loop the tree forever: park them visibly.
                if (!ReferenceEquals(target, root) && !ReferenceEquals(target, orphans)
                    && cycles.Contains(recNo))
                    target = orphans;
                target.AddChild(node);
            }

            // Safety net (should be a no-op after ResolveTarget): legacy second loop.
            for (int i = 0; i < SystemRecordCount && i < size; i++)
            {
                if (i == RootRecordNumber) continue;
                if (slots[i] is { } sys && sys.Parent is null)
                    root.AddChild(sys);
            }

            if (orphans.Children?.Count > 0) root.AddChild(orphans);
            return root;
        }
        finally
        {
            ArrayPool<FsNode?>.Shared.Return(slots, clearArray: true);
            ArrayPool<int>.Shared.Return(counts, clearArray: true);
        }
    }

    // Sparse/deleted-heavy fallback: original dictionary materialization (rare; keeps behavior exact).
    private static FsNode BuildTreeSparse(Dictionary<long, MftEntryInfo> entries, VolumeInfo capacity, HashSet<long> cycles, CancellationToken ct)
    {
        var nodes = new Dictionary<long, FsNode>(entries.Count);

        foreach ((long recNo, MftEntryInfo e) in entries)
        {
            ct.ThrowIfCancellationRequested();
            bool isSystem = recNo < SystemRecordCount;
            string name = e.HasFileName && e.Name.Length > 0 ? e.Name
                : isSystem && recNo >= 0 && SystemNames[(int)recNo].Length > 0 ? SystemNames[(int)recNo]
                : $"#{recNo}";

            var node = new FsNode
            {
                Name = name,
                FileKey = unchecked((long)(((ulong)e.SequenceNumber << 48) | (ulong)recNo)),
                    VolumeKey = capacity.VolumeSerialNumber,
            };
            ApplyInfo(node, e, capacity.BytesPerCluster);
            nodes[recNo] = node;
        }

        FsNode root = nodes.TryGetValue(RootRecordNumber, out FsNode? rootFromMft)
            ? rootFromMft
            : new FsNode { Name = "(root)", FileKey = RootRecordNumber };
        root.Name = capacity.RootPath;

        var orphans = new FsNode
        {
            Name = "[orphaned]",
            Flags = NodeFlags.Directory | NodeFlags.OrphanedRoot,
        };

        foreach ((long recNo, FsNode node) in nodes)
        {
            ct.ThrowIfCancellationRequested();
            if (recNo == RootRecordNumber || node.Parent is not null) continue;
            MftEntryInfo e = entries[recNo];

            long parentRec = unchecked((long)e.ParentRecordNumber);
            if (parentRec != recNo && nodes.TryGetValue(parentRec, out FsNode? parent))
            {
                // Only mount points and symlinks stay detached (GenericScanner doesn't
                // enumerate inside them, so attaching here would double-count). Other
                // reparse dirs — incl. cloud placeholders — attach normally.
                // Left detached = invisible, same as generic.
                if ((parent.Flags & (NodeFlags.Directory | NodeFlags.ReparsePoint))
                    == (NodeFlags.Directory | NodeFlags.ReparsePoint)
                    && IsMountOrSymlinkTarget(parentRec, entries))
                    continue;
                // A non-directory parent cannot contain children: corrupt parent refs
                // attach system records to the root and route the rest to orphans.
                if (entries.TryGetValue(parentRec, out MftEntryInfo parentInfo) && !parentInfo.IsDirectory)
                {
                    if (recNo >= SystemRecordCount) orphans.AddChild(node);
                    else root.AddChild(node);
                }
                else if (cycles.Contains(recNo))
                {
                    orphans.AddChild(node);
                }
                else parent.AddChild(node);
            }
            else if (parentRec == recNo)
            {
                // Self-referential (system files do this): attach non-root ones to the root.
                if (recNo >= SystemRecordCount) root.AddChild(node);
            }
            else if (recNo >= SystemRecordCount)
            {
                orphans.AddChild(node);
            }
            else if (parentRec != 0)
            {
                root.AddChild(node); // missing parent record (deleted dir etc.)
            }
            else
            {
                orphans.AddChild(node);
            }
        }

        foreach (int i in Enumerable.Range(0, SystemRecordCount))
        {
            if (i == RootRecordNumber) continue;
            if (nodes.TryGetValue(i, out FsNode? sys) && sys.Parent is null)
                root.AddChild(sys);
        }

        if (orphans.Children?.Count > 0) root.AddChild(orphans);
        return root;
    }

    internal static void ApplyInfo(FsNode node, in MftEntryInfo e, uint clusterSize)
    {
        if (e.Reparse) node.Flags |= NodeFlags.ReparsePoint;
        node.StreamAllocatedSize = e.AdsAllocatedSize;
        node.MetadataAllocatedSize = e.IndexAllocationSize;
        node.AllocatedSize = TreeOps.SaturatingAdd(TreeOps.SaturatingAdd(e.DataAllocatedSize, e.AdsAllocatedSize), e.IndexAllocationSize);

        if (e.IsDirectory)
        {
            node.Flags |= NodeFlags.Directory;
            node.LogicalSize = 0;
        }
        else
        {
            node.LogicalSize = e.LogicalSize;
            // Resident streams occupy the MFT record and require no separate clusters;
            // $MFT itself is represented in the tree and accounts for that storage.
            // System files like $Secure/$ObjId carry directory-index allocation
            // without being directories; count it so their storage isn't lost.

            if (e.Compressed) node.Flags |= NodeFlags.Compressed;
            if (e.Sparse) node.Flags |= NodeFlags.SparseFile;

            // Recall-marked but resident files (seen live: payloads carrying
            // RECALL_ON_* with fully allocated runs) bill their parsed allocation:
            // that number is on-disk truth, and dehydrated placeholders already
            // report AllocationSize 0 so they are unaffected. Mirrors generic.
            if (e.Offline) node.Flags |= NodeFlags.CloudPlaceholder;
        }

        // All real names survive; shared accounting selects the owner within scan scope.
        if ((e.FileNameLinks > 1 || e.NameLinks is { Count: > 1 }) && !e.IsDirectory) node.Flags |= NodeFlags.Hardlinked;
    }
}

public static class MftMath
{
    public static ulong RoundToCluster(ulong bytes, uint clusterSize) =>
        clusterSize <= 1 ? bytes : (bytes + clusterSize - 1) / clusterSize * clusterSize;
}
