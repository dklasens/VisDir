using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VisDir.Core.Interop;

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

    public ScanResult Scan(ScanOptions options, CancellationToken cancellationToken, IProgress<ScanProgress>? progress)
    {
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

            Dictionary<long, MftEntryInfo> entries = ReadMft(hVolume, volumeDevice, in vol, cancellationToken, progress);
            FsNode root = BuildTree(entries, capacity);
            TreeOps.Finalize(root);

            // The MFT read is always volume-wide; narrow the returned tree to the requested
            // subpath so callers see the same root contract as the compatible scanner.
            FsNode pruned = PruneToRequestedPath(root, requestedPath);
            (long files, long dirs) = CountSubtree(pruned);

            return new ScanResult
            {
                Volume = capacity with
                {
                    BytesPerCluster = vol.BytesPerCluster,
                    FileSystemName = "NTFS",
                },
                Root = pruned,
                EngineName = "mft",
                Stats = new ScanStats
                {
                    FileCount = files,
                    DirectoryCount = dirs,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                },
            };
        }
        finally
        {
            NativeMethods.CloseHandle(hVolume);
        }
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
    private static (long Files, long Dirs) CountSubtree(FsNode root)
    {
        long files = 0, dirs = 0;
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            FsNode node = stack.Pop();
            if (node.IsDirectory) dirs++;
            else files++;
            if (node.Children is { } kids)
                foreach (FsNode kid in kids)
                    stack.Push(kid);
        }
        return (files, dirs);
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

        ulong validLength = Math.Min(vol.MftValidDataLength, vol.TotalClusters * vol.BytesPerCluster);

        // Size for the estimated live-record count so small volumes don't pay 1M slots
        // up front; sparse/deleted-heavy MFTs still grow naturally from here.
        var entries = new Dictionary<long, MftEntryInfo>(
            capacity: (int)Math.Clamp(validLength / recordSize, 1024, 1 << 20));
        var extensionRecNos = new List<long>(1024);
        var buffer = GC.AllocateUninitializedArray<byte>(ReadBufferSize);
        bool trace = Environment.GetEnvironmentVariable("VISDIR_TRACE_ERRORS") == "1";
        long nSlots = 0, nMagic = 0, nFixup = 0, nStruct = 0, nNotInUse = 0;

        long recordsSeen = 0;
        ulong bytesProcessed = 0;
        var progressClock = Stopwatch.StartNew();
        long lastReportMs = -250; // first chunk reports immediately, then >=250ms apart

        // Tier 1: open $MFT through the filesystem namespace — the kernel resolves
        // fragmentation/attribute-lists for us, so plain sequential reads suffice.
        // Backup semantics lets admin/backup-privileged callers past its restrictive ACL.
        // Tier 2 (fallback): decode $MFT record #0's run list from raw volume reads.
        IntPtr hMft = NativeMethods.CreateFileW(
            $"{volumeDevice}\\$MFT",
            NtfsNative.GenericRead, NtfsNative.ShareReadWriteDelete,
            IntPtr.Zero, NtfsNative.OpenExisting,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

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
                void ParseBuffer(byte* basePtr, uint bytes)
                {
                    uint usable = bytes - bytes % recordSize;
                    for (uint off = 0; off + recordSize <= usable; off += recordSize)
                    {
                        byte* rec = basePtr + off;
                        nSlots++;
                        if (*rec != (byte)'F') continue; // unused/corrupt slot fast-path
                        nMagic++;

                        if (!NtfsRecordParser.TryParseRecord(rec, (int)recordSize, out MftEntryInfo info, out byte stage))
                        {
                            if (stage == NtfsRecordParser.FailFixup) nFixup++;
                            else if (stage == NtfsRecordParser.FailStructure) nStruct++;
                            continue;
                        }
                        if (!info.InUse) { nNotInUse++; continue; }

                        // $BadClus's $DATA intentionally spans the whole volume.
                        if (info.RecordNumber == BadClusRecordNumber)
                        {
                            info.LogicalSize = info.DataAllocatedSize = info.AdsAllocatedSize = 0;
                        }

                        entries[info.RecordNumber] = info;
                        if (info.BaseRecordNumber != 0 && info.BaseRecordNumber != info.RecordNumber)
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
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        uint want = (uint)Math.Min(remaining, (uint)buffer.Length);
                        if (!NtfsNative.ReadFile(hMft, bufPtr, want, out uint got, IntPtr.Zero) || got == 0)
                            break;
                        ParseBuffer(bufPtr, got);
                        ReportProgress(got);
                        remaining -= Math.Min(remaining, got);
                    }
                }
                else
                {
                    List<(long StartLcn, ulong Clusters)> extents =
                        DiscoverMftExtents(hVolume, in vol, validLength);

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
                            if (!NtfsNative.ReadFile(hVolume, bufPtr, want, out uint got, IntPtr.Zero) || got == 0)
                            {
                                remaining = 0;
                                break;
                            }

                            ParseBuffer(bufPtr, got);
                            ReportProgress(got);
                            pos += got;
                            extentBytes -= got;
                            remaining -= Math.Min(remaining, got);
                        }
                    }
                }

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
                $"notInUse={nNotInUse} kept={entries.Count}");
        }

        MergeExtensionRecords(entries, extensionRecNos);
        return entries;
    }

    /// <summary>
    /// Records with a non-zero base reference are $ATTRIBUTE_LIST extensions of their base
    /// record (huge/fragmented files spill $DATA instances there, without a $FILE_NAME).
    /// Fold their sizes into the base so tree assembly sees one complete node per file.
    /// </summary>
    private static void MergeExtensionRecords(Dictionary<long, MftEntryInfo> entries, List<long> extensionRecNos)
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
            baseInfo.AdsAllocatedSize += ext.AdsAllocatedSize;
            baseInfo.IndexAllocationSize += ext.IndexAllocationSize;
            baseInfo.Compressed |= ext.Compressed;
            baseInfo.Sparse |= ext.Sparse;
            baseInfo.HasPrimaryData |= ext.HasPrimaryData;
            baseInfo.Reparse |= ext.Reparse;
            baseInfo.Offline |= ext.Offline;
        }

        foreach (long recNo in extensionRecNos)
        {
            if (!entries.TryGetValue(recNo, out MftEntryInfo ext)) continue;
            if (ext.BaseRecordNumber == 0 || ext.BaseRecordNumber == recNo) continue;
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
        IntPtr hVolume, in NtfsNative.NtfsVolumeDataBuffer vol, ulong validLength)
    {
        if (vol.BytesPerCluster == 0)
            throw new IOException("Invalid NTFS geometry: zero bytes per cluster.");
        if (vol.MftStartLcn >= vol.TotalClusters)
            throw new IOException(
                $"Invalid MFT start LCN {vol.MftStartLcn} (total clusters {vol.TotalClusters}).");
        ulong availClusters = vol.TotalClusters - vol.MftStartLcn; // >= 1 from the check above

        uint recordSize = vol.BytesPerFileRecordSegment;
        var probe = GC.AllocateUninitializedArray<byte>((int)recordSize * 8); // first few records

        fixed (byte* p = probe)
        {
            if (!NtfsNative.SetFilePointerEx(hVolume, checked((long)(vol.MftStartLcn * vol.BytesPerCluster)), out _, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NtfsNative.ReadFile(hVolume, p, (uint)probe.Length, out uint got, IntPtr.Zero) || got < recordSize)
                throw new IOException("Failed to read $MFT record 0.");

            var extents = new List<(long, ulong)>();
            // NOTE: TryDecodeDataRuns applies the update-sequence fixup itself; running a
            // parser pass first would corrupt the sector-tail words and fail validation.
            if (NtfsRecordParser.TryDecodeDataRuns(p, (int)recordSize, extents))
            {
                return extents;
            }
        }

        // Degenerate fallback: treat the whole MFT as one contiguous span from its start
        // LCN, clamped to the clusters that actually exist. Zero means nothing to read —
        // return empty instead of forcing a past-the-end cluster.
        ulong fallbackClusters = Math.Min(
            (validLength + vol.BytesPerCluster - 1) / vol.BytesPerCluster,
            availClusters);
        if (fallbackClusters == 0) return new List<(long, ulong)>();
        return new List<(long, ulong)> { ((long)vol.MftStartLcn, fallbackClusters) };
    }

    private static readonly string[] SystemNames =
    {
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "", "$Bitmap", "$Boot",
        "$BadClus", "$Secure", "$UpCase", "$Extend", "$Reserved12", "$Reserved13",
        "$Reserved14", "$Reserved15",
    };

    private static FsNode BuildTree(Dictionary<long, MftEntryInfo> entries, VolumeInfo capacity)
    {
        var nodes = new Dictionary<long, FsNode>(entries.Count);

        foreach ((long recNo, MftEntryInfo e) in entries)
        {
            bool isSystem = recNo < SystemRecordCount;
            string name = e.HasFileName && e.Name.Length > 0 ? e.Name
                : isSystem && SystemNames[recNo].Length > 0 ? SystemNames[recNo]
                : $"#{recNo}";

            var node = new FsNode
            {
                Name = name,
                FileKey = recNo,
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
            if (recNo == RootRecordNumber || node.Parent is not null) continue;
            MftEntryInfo e = entries[recNo];

            long parentRec = unchecked((long)e.ParentRecordNumber);
            if (parentRec != recNo && nodes.TryGetValue(parentRec, out FsNode? parent))
            {
                // Never descend into reparse-point directories (junctions/symlinks):
                // GenericScanner doesn't enumerate inside them, so attaching their
                // contents here would double-count. Left detached = invisible, same as generic.
                if ((parent.Flags & (NodeFlags.Directory | NodeFlags.ReparsePoint))
                    == (NodeFlags.Directory | NodeFlags.ReparsePoint))
                    continue;
                parent.AddChild(node);
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

    private static void ApplyInfo(FsNode node, in MftEntryInfo e, uint clusterSize)
    {
        if (e.Reparse) node.Flags |= NodeFlags.ReparsePoint;

        if (e.IsDirectory)
        {
            node.Flags |= NodeFlags.Directory;
            node.AllocatedSize = e.IndexAllocationSize; // directory index overhead
            node.LogicalSize = 0;
        }
        else
        {
            node.LogicalSize = e.LogicalSize;
            // Resident streams occupy the MFT record and require no separate clusters;
            // $MFT itself is represented in the tree and accounts for that storage.
            node.AllocatedSize = e.DataAllocatedSize + e.AdsAllocatedSize;

            if (e.Compressed) node.Flags |= NodeFlags.Compressed;
            if (e.Sparse) node.Flags |= NodeFlags.SparseFile;

            // Cloud placeholders aren't resident locally — their allocation numbers
            // describe remote content. Mirrors GenericScanner.ApplyAttributes.
            if (e.Offline)
            {
                node.Flags |= NodeFlags.CloudPlaceholder;
                node.AllocatedSize = 0;
            }
        }

        // Hardlinks: sizes counted once at the canonical (first-seen) link location;
        // extra links simply don't exist as separate nodes.
        if (e.FileNameLinks > 1 && !e.IsDirectory) node.Flags |= NodeFlags.Hardlinked;
    }
}

public static class MftMath
{
    public static ulong RoundToCluster(ulong bytes, uint clusterSize) =>
        clusterSize <= 1 ? bytes : (bytes + clusterSize - 1) / clusterSize * clusterSize;
}
