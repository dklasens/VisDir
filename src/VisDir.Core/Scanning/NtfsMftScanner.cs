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

    // Parent-reference cycle probe (corrupt MFT): visited-set walk up at most 64 hops.
    // Terminates at the root, a self-ref, or a missing parent; over-long chains count
    // as acyclic so legit deep trees are never orphaned.
    private static bool HasParentCycle(long recNo, Dictionary<long, MftEntryInfo> entries)
    {
        Span<long> trail = stackalloc long[64];
        int depth = 0;
        long cur = recNo;
        while (depth < 64)
        {
            for (int i = 0; i < depth; i++)
                if (trail[i] == cur) return true;
            trail[depth++] = cur;
            if (!entries.TryGetValue(cur, out MftEntryInfo e)) return false;
            long parent = unchecked((long)e.ParentRecordNumber);
            if (parent == cur) return false;
            if (parent == RootRecordNumber) return false;
            cur = parent;
        }
        return false;
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
        long nSlots = 0, nMagic = 0, nFixup = 0, nStruct = 0, nNotInUse = 0, nDup = 0;

        long recordsSeen = 0;
        ulong bytesProcessed = 0;
        var progressClock = Stopwatch.StartNew();
        long lastReportMs = -250; // first chunk reports immediately, then >=250ms apart
        // Tier 1: open $MFT through the filesystem namespace — the kernel resolves
        // fragmentation/attribute-lists for us, so plain sequential reads suffice.
        // Backup semantics lets admin/backup-privileged callers past its restrictive ACL.
        // OVERLAPPED allows the Tier-1 double-buffer below (chunk N+1 reads while N parses);
        // synchronous fallback on the same handle stays correct when overlapped is unavailable.
        // Tier 2 (fallback): decode $MFT record #0's run list from raw volume reads.
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
                        byte* rec = basePtr + off;
                        nSlots++;
                        if (*rec != (byte)'F') continue; // unused/corrupt slot fast-path
                        nMagic++;
                        // Cheap pre-parse: the in-use flag sits at a fixed offset, so skip
                        // dead slots without walking attributes (post-parse check stays).
                        if (recordSize >= 0x18 && (*(ushort*)(rec + 0x16) & 1) == 0) { nNotInUse++; continue; }

                        if (!NtfsRecordParser.TryParseRecord(rec, (int)recordSize, out MftEntryInfo info, out byte stage))
                        {
                            if (stage == NtfsRecordParser.FailFixup) nFixup++;
                            else if (stage == NtfsRecordParser.FailStructure) nStruct++;
                            continue;
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
                    // Double-buffered overlapped Tier-1: chunk N+1 reads while N parses.
                    // Falls back to the original synchronous loop when overlapped is unavailable;
                    // Tier-2 (volume extents) below is untouched.
                    bool pipelined = false;
                    byte[]? buffer2 = null;
                    try { buffer2 = GC.AllocateUninitializedArray<byte>(ReadBufferSize); }
                    catch (OutOfMemoryException) { buffer2 = null; }

                    if (buffer2 is not null)
                    {
                        fixed (byte* bufPtr2 = buffer2)
                        {
                            IntPtr evA = NtfsNative.CreateEventW(IntPtr.Zero, false, false, null);
                            IntPtr evB = NtfsNative.CreateEventW(IntPtr.Zero, false, false, null);
                            if (evA != IntPtr.Zero && evB != IntPtr.Zero)
                            {
                                try
                                {
                                    var ovA = new NtfsNative.OVERLAPPED { hEvent = evA };
                                    var ovB = new NtfsNative.OVERLAPPED { hEvent = evB };
                                    ulong fileOff = 0;
                                    bool failed = false;
                                    uint wantCur = (uint)Math.Min(validLength - fileOff, (uint)buffer.Length);
                                    if (wantCur == 0)
                                    {
                                        pipelined = true;
                                    }
                                    else
                                    {
                                        byte* curPtr = bufPtr;
                                        byte* nxtPtr = bufPtr2;
                                        NtfsNative.OVERLAPPED* pCur = &ovA;
                                        NtfsNative.OVERLAPPED* pNxt = &ovB;
                                        int stCur;
                                        uint immCur;
                                        ct.ThrowIfCancellationRequested();
                                        stCur = StartOverlappedRead(hMft, curPtr, wantCur, fileOff, pCur, out immCur);
                                        if (stCur == 2)
                                        {
                                            failed = true;
                                        }
                                        else
                                        {
                                            fileOff += wantCur;
                                            ulong curBase = fileOff - wantCur; // MFT-logical offset of curPtr
                                            while (true)
                                            {
                                                uint gotCur;
                                                if (!FinishOverlappedRead(hMft, pCur, stCur, immCur, out gotCur))
                                                {
                                                    failed = true;
                                                    break;
                                                }
                                                ct.ThrowIfCancellationRequested();
                                                if (gotCur == 0) break;
                                                bool haveNext = false;
                                                uint wantNxt = 0;
                                                int stNxt = 0;
                                                uint immNxt = 0;
                                                ulong nxtBase = 0;
                                                if (validLength - fileOff > 0)
                                                {
                                                    wantNxt = (uint)Math.Min(validLength - fileOff, (uint)buffer.Length);
                                                    nxtBase = fileOff;
                                                    stNxt = StartOverlappedRead(hMft, nxtPtr, wantNxt, fileOff, pNxt, out immNxt);
                                                    if (stNxt == 2) failed = true;
                                                    else { fileOff += wantNxt; haveNext = true; }
                                                }
                                                ParseBuffer(curPtr, gotCur, curBase);
                                                ReportProgress(gotCur);
                                                remaining -= Math.Min(remaining, gotCur);
                                                if (gotCur < wantCur)
                                                {
                                                    // `want` is always clamped to `validLength - fileOff`, so a
                                                    // short-of-want read is an anomaly, never a legitimate EOF
                                                    // (at true EOF got == want and this never triggers). Flag it
                                                    // so the synchronous resume below re-reads from
                                                    // `bytesProcessed`; worst case that resume repeats bytes.
                                                    failed = true;
                                                    if (haveNext)
                                                    {
                                                        uint dummy;
                                                        FinishOverlappedRead(hMft, pNxt, stNxt, immNxt, out dummy);
                                                    }
                                                    break;
                                                }
                                                if (!haveNext) break;
                                                byte* tmpPtr = curPtr; curPtr = nxtPtr; nxtPtr = tmpPtr;
                                                NtfsNative.OVERLAPPED* tmpOv = pCur; pCur = pNxt; pNxt = tmpOv;
                                                wantCur = wantNxt; stCur = stNxt; immCur = immNxt; curBase = nxtBase;
                                            }
                                        }
                                    }
                                    if (!failed || remaining == 0) pipelined = true;
                                }
                                finally
                                {
                                    NativeMethods.CloseHandle(evA);
                                    NativeMethods.CloseHandle(evB);
                                }
                            }
                            else
                            {
                                if (evA != IntPtr.Zero) NativeMethods.CloseHandle(evA);
                                if (evB != IntPtr.Zero) NativeMethods.CloseHandle(evB);
                            }
                        }
                    }

                    if (!pipelined)
                    {
                        // Synchronous resume: overlapped uses explicit offsets and never moves the
                        // file pointer, so seek to the parsed offset before continuing linearly.
                        if (bytesProcessed > 0)
                        {
                            if (!NtfsNative.SetFilePointerEx(hMft, (long)bytesProcessed, out _, 0))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            remaining = validLength - Math.Min(validLength, bytesProcessed);
                        }
                        ulong syncBase = bytesProcessed; // MFT-logical offset of the next chunk
                        while (remaining > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            uint want = (uint)Math.Min(remaining, (uint)buffer.Length);
                            // `want` is clamped to `remaining`, so ReadFile-false/got==0 with
                            // bytes left is an anomaly, never EOF (normal end exits via the
                            // `remaining > 0` condition). Fail closed: the existing
                            // MFT→generic fallback then produces a correct tree instead of a
                            // silently partial one.
                            if (!NtfsNative.ReadFile(hMft, bufPtr, want, out uint got, IntPtr.Zero) || got == 0)
                                throw new IOException(
                                    $"Short MFT read at offset {syncBase}: wanted {want}, got {got} (remaining {remaining}).");
                            ParseBuffer(bufPtr, got, syncBase);
                            syncBase += got;
                            ReportProgress(got);
                            remaining -= Math.Min(remaining, got);
                        }
                    }
                }
                else
                {
                    List<(long StartLcn, ulong Clusters)> extents =
                        DiscoverMftExtents(hVolume, in vol, validLength);

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
                            if (!NtfsNative.ReadFile(hVolume, bufPtr, want, out uint got, IntPtr.Zero) || got == 0)
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

        MergeExtensionRecords(entries, extensionRecNos);
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

    // Overlapped Tier-1 helpers: Start returns 0=sync-completed, 1=pending, 2=hard failure.
    private static unsafe int StartOverlappedRead(
        IntPtr h, byte* buf, uint want, ulong fileOffset, NtfsNative.OVERLAPPED* ov, out uint immediateGot)
    {
        ov->Internal = UIntPtr.Zero;
        ov->InternalHigh = UIntPtr.Zero;
        ov->Offset = (uint)(fileOffset & 0xFFFFFFFFUL);
        ov->OffsetHigh = (uint)(fileOffset >> 32);
        // hEvent is set once by the caller and preserved here.
        if (NtfsNative.ReadFile(h, buf, want, out immediateGot, (IntPtr)ov))
            return 0;
        return Marshal.GetLastWin32Error() == NtfsNative.ErrorIoPending ? 1 : 2;
    }

    private static unsafe bool FinishOverlappedRead(
        IntPtr h, NtfsNative.OVERLAPPED* ov, int started, uint immediateGot, out uint got)
    {
        if (started == 0) { got = immediateGot; return true; }
        if (NtfsNative.WaitForSingleObject(ov->hEvent, NtfsNative.Infinite) != 0) { got = 0; return false; }
        return NtfsNative.GetOverlappedResult(h, ov, out got, false);
    }

    /// <summary>
    /// Records with a non-zero base reference are $ATTRIBUTE_LIST extensions of their base
    /// record (huge/fragmented files spill $DATA instances there, without a $FILE_NAME).
    /// Fold their sizes into the base so tree assembly sees one complete node per file.
    /// </summary>
    internal static void MergeExtensionRecords(Dictionary<long, MftEntryInfo> entries, List<long> extensionRecNos)
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
        // Single materialization: node refs live in one pooled record-keyed array instead of a
        // second Dictionary<long,FsNode>. Peak is entries-dict + array (8 B/slot, pooled),
        // not dict+dict. The array is returned after linking; the tree keeps refs via Parent/Children.
        long maxRec = 0;
        foreach (long k in entries.Keys)
            if (k > maxRec) maxRec = k;
        if (maxRec < 0 || maxRec > int.MaxValue - 1 || maxRec > (long)entries.Count * 8L + 4096 || maxRec > 32_000_000L)
            return BuildTreeSparse(entries, capacity);

        int size = (int)maxRec + 1;
        FsNode?[] slots = ArrayPool<FsNode?>.Shared.Rent(size);
        Array.Clear(slots, 0, size);
        int[] counts = ArrayPool<int>.Shared.Rent(size);
        Array.Clear(counts, 0, size);
        try
        {
            foreach ((long recNo, MftEntryInfo e) in entries)
            {
                if (recNo < 0 || recNo >= size) continue; // defensive: rec nos are dense uints
                bool isSystem = recNo < SystemRecordCount;
                string name = e.HasFileName && e.Name.Length > 0 ? e.Name
                    : isSystem && SystemNames[(int)recNo].Length > 0 ? SystemNames[(int)recNo]
                    : $"#{recNo}";
                var node = new FsNode
                {
                    Name = name,
                    FileKey = recNo,
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
                if (recNo == RootRecordNumber || recNo < 0 || recNo >= size) continue;
                FsNode? node = slots[(int)recNo];
                if (node is null || node.Parent is not null) continue;
                FsNode? target = ResolveTarget(recNo, slots, size, entries, root, orphans);
                if (target is null) continue;
                // Corrupt parent cycles would loop the tree forever: park them visibly.
                if (!ReferenceEquals(target, root) && !ReferenceEquals(target, orphans)
                    && HasParentCycle(recNo, entries))
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
    private static FsNode BuildTreeSparse(Dictionary<long, MftEntryInfo> entries, VolumeInfo capacity)
    {
        var nodes = new Dictionary<long, FsNode>(entries.Count);

        foreach ((long recNo, MftEntryInfo e) in entries)
        {
            bool isSystem = recNo < SystemRecordCount;
            string name = e.HasFileName && e.Name.Length > 0 ? e.Name
                : isSystem && recNo >= 0 && SystemNames[(int)recNo].Length > 0 ? SystemNames[(int)recNo]
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
                else if (HasParentCycle(recNo, entries))
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
            // System files like $Secure/$ObjId carry directory-index allocation
            // without being directories; count it so their storage isn't lost.
            if (e.IndexAllocationSize > 0) node.AllocatedSize += e.IndexAllocationSize;

            if (e.Compressed) node.Flags |= NodeFlags.Compressed;
            if (e.Sparse) node.Flags |= NodeFlags.SparseFile;

            // Recall-marked but resident files (seen live: payloads carrying
            // RECALL_ON_* with fully allocated runs) bill their parsed allocation:
            // that number is on-disk truth, and dehydrated placeholders already
            // report AllocationSize 0 so they are unaffected. Mirrors generic.
            if (e.Offline) node.Flags |= NodeFlags.CloudPlaceholder;
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
