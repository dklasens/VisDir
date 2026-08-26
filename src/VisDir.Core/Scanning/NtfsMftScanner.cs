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
/// whole volume tree from raw records. Requires elevation; NTFS volumes only.
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

            return new ScanResult
            {
                Volume = capacity with
                {
                    BytesPerCluster = vol.BytesPerCluster,
                    FileSystemName = "NTFS",
                },
                Root = root,
                EngineName = "mft",
                Stats = new ScanStats
                {
                    FileCount = entries.Values.Count(e => !e.IsDirectory && e.InUse),
                    DirectoryCount = entries.Values.Count(e => e.IsDirectory && e.InUse),
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                },
            };
        }
        finally
        {
            NativeMethods.CloseHandle(hVolume);
        }
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

        var entries = new Dictionary<long, MftEntryInfo>(capacity: 1 << 20);
        var extensionRecNos = new List<long>(1024);
        var buffer = GC.AllocateUninitializedArray<byte>(ReadBufferSize);
        bool trace = Environment.GetEnvironmentVariable("VISDIR_TRACE_ERRORS") == "1";
        long nSlots = 0, nMagic = 0, nFixup = 0, nStruct = 0, nNotInUse = 0;

        long recordsSeen = 0;
        ulong bytesProcessed = 0;
        var progressClock = Stopwatch.StartNew();

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

                void ReportProgress(uint bytes)
                {
                    bytesProcessed += bytes;
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
        var removed = new HashSet<long>();
        bool changed = true;
        int passes = 0;

        // Iterate to a fixed point so chained extensions (ext -> ext -> base) fold fully.
        while (changed && passes++ < 8)
        {
            changed = false;
            foreach (long recNo in extensionRecNos)
            {
                if (removed.Contains(recNo)) continue;
                if (!entries.TryGetValue(recNo, out MftEntryInfo ext)) continue;
                if (ext.BaseRecordNumber == 0 || ext.BaseRecordNumber == recNo) continue;
                if (removed.Contains(ext.BaseRecordNumber) || !entries.TryGetValue(ext.BaseRecordNumber, out MftEntryInfo baseInfo))
                    continue;

                baseInfo.LogicalSize = Math.Max(baseInfo.LogicalSize, ext.LogicalSize);
                baseInfo.DataAllocatedSize += ext.DataAllocatedSize;
                baseInfo.AdsAllocatedSize += ext.AdsAllocatedSize;
                baseInfo.IndexAllocationSize += ext.IndexAllocationSize;
                baseInfo.Compressed |= ext.Compressed;
                baseInfo.Sparse |= ext.Sparse;
                baseInfo.HasPrimaryData |= ext.HasPrimaryData;
                entries[ext.BaseRecordNumber] = baseInfo;

                removed.Add(recNo);
                changed = true;
            }
        }

        foreach (long recNo in removed) entries.Remove(recNo);
    }

    /// <summary>
    /// Reads $MFT record #0 from the known start LCN and decodes its cluster-run list so
    /// fragmented MFTs are read correctly. Falls back to a single sequential extent when
    /// decoding is not possible (degenerate volumes).
    /// </summary>
    private static unsafe List<(long StartLcn, ulong Clusters)> DiscoverMftExtents(
        IntPtr hVolume, in NtfsNative.NtfsVolumeDataBuffer vol, ulong validLength)
    {
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

        // Degenerate fallback: treat the whole MFT as one contiguous span from its start LCN.
        ulong fallbackClusters = Math.Min(
            (validLength + vol.BytesPerCluster - 1) / vol.BytesPerCluster,
            vol.TotalClusters - vol.MftStartLcn);
        return new List<(long, ulong)> { ((long)vol.MftStartLcn, Math.Max(fallbackClusters, 1)) };
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
        if (e.IsDirectory)
        {
            node.Flags |= NodeFlags.Directory;
            node.AllocatedSize = e.IndexAllocationSize; // directory index overhead
            node.LogicalSize = 0;
        }
        else
        {
            node.LogicalSize = e.LogicalSize;
            node.AllocatedSize = e.PrimaryDataResident
                ? MftMath.RoundToCluster(e.DataAllocatedSize, clusterSize) + e.AdsAllocatedSize
                : e.DataAllocatedSize + e.AdsAllocatedSize;

            if (e.Compressed) node.Flags |= NodeFlags.Compressed;
            if (e.Sparse) node.Flags |= NodeFlags.SparseFile;
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
