using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using VisDir.Core;
using VisDir.Core.Scanning;
using VisDir.App.Sunburst;
using VisDir.App.Update;
using Xunit;

namespace VisDir.Core.Tests;

public class TreeSerializerTests
{
    private static ScanResult MakeSample()
    {
        var root = new FsNode { Name = "C:\\", Flags = NodeFlags.Directory };
        var users = new FsNode { Name = "Users", Flags = NodeFlags.Directory, AllocatedSize = 0 };
        var bigFile = new FsNode { Name = "big.iso", LogicalSize = 1000, AllocatedSize = 4096 };
        var small = new FsNode
        {
            Name = "small.txt",
            LogicalSize = 10,
            AllocatedSize = 4096,
            Flags = NodeFlags.Hidden,
            FileKey = 12345,
        };
        root.AddChild(users);
        users.AddChild(bigFile);
        users.AddChild(small);
        var programData = new FsNode { Name = "ProgramData", Flags = NodeFlags.Directory };
        programData.AddChild(new FsNode { Name = "cache.bin", LogicalSize = 500, AllocatedSize = 1024 });
        root.AddChild(programData);
        root.AddChild(new FsNode { Name = "empty", Flags = NodeFlags.Directory });
        root.AddChild(new FsNode { Name = "boot.dat", LogicalSize = 100, AllocatedSize = 512 });
        TreeOps.Finalize(root);

        return new ScanResult
        {
            Volume = new VolumeInfo
            {
                RootPath = "C:\\",
                DisplayName = "Windows",
                FileSystemName = "NTFS",
                VolumeSerialNumber = 0xAABBCCDD,
                BytesPerCluster = 4096,
                TotalBytes = 100_000_000,
                FreeBytes = 50_000_000,
            },
            Root = root,
            EngineName = "unit-test",
            Stats = new ScanStats
            {
                FileCount = 4,
                DirectoryCount = 4,
                ErrorCount = 3,
                BytesSeen = 9_728,
                ElapsedMs = 1234.5,
            },
        };
    }

    [Fact]
    public void RoundTrip_PreservesTreeStructureAndTotals()
    {
        ScanResult original = MakeSample();

        using var ms = new MemoryStream();
        TreeSerializer.Write(ms, original);
        ms.Position = 0;
        ScanResult loaded = TreeSerializer.Read(ms);

        Assert.Equal(original.Volume.RootPath, loaded.Volume.RootPath);
        Assert.Equal(original.Volume.FileSystemName, loaded.Volume.FileSystemName);
        Assert.Equal(original.Volume.BytesPerCluster, loaded.Volume.BytesPerCluster);
        Assert.Equal(original.Volume.TotalBytes, loaded.Volume.TotalBytes);

        Assert.Equal(4, loaded.Root.Children!.Count);
        Assert.Equal(new[] { "Users", "ProgramData", "boot.dat", "empty" }, loaded.Root.Children.Select(n => n.Name));
        Assert.Equal(original.Root.TotalAllocated, loaded.Root.TotalAllocated);

        FsNode users = loaded.Root.Children[0];
        Assert.Equal(2, users.Children!.Count);
        // Sorted descending: big.iso (4096) before small.txt (4096) — tie broken by name ordinal
        Assert.Equal("big.iso", users.Children[0].Name);
        Assert.Equal("small.txt", users.Children[1].Name);
        Assert.Equal(NodeFlags.Hidden, users.Children[1].Flags & NodeFlags.Hidden);
        Assert.Equal(12345, users.Children[1].FileKey);
        Assert.Same(users, users.Children[0].Parent);
        Assert.Single(loaded.Root.Children[1].Children!);
        Assert.Empty(loaded.Root.Children[3].Children ?? []);
        Assert.Equal("unit-test", loaded.EngineName);
        Assert.Equal(4, loaded.Stats.FileCount);
        Assert.Equal(4, loaded.Stats.DirectoryCount);
        Assert.Equal(3, loaded.Stats.ErrorCount);
        Assert.Equal(9_728UL, loaded.Stats.BytesSeen);
        Assert.Equal(1234.5, loaded.Stats.ElapsedMs);
    }

    [Fact]
    public void RoundTrip_RejectsGarbageMagic()
    {
        var bad = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<InvalidDataException>(() => TreeSerializer.Read(bad));
    }

    [Fact]
    public void RoundTrip_HandlesDeepTreesWithoutRecursion()
    {
        var root = new FsNode { Name = "root", Flags = NodeFlags.Directory };
        FsNode cursor = root;
        for (int i = 0; i < 10_000; i++)
        {
            var child = new FsNode { Name = $"d{i}", Flags = NodeFlags.Directory };
            cursor.AddChild(child);
            cursor = child;
        }
        cursor.AddChild(new FsNode { Name = "leaf", LogicalSize = 1, AllocatedSize = 4_096 });
        TreeOps.Finalize(root);

        var result = new ScanResult
        {
            Volume = SampleVolume(), Root = root, EngineName = "deep",
            Stats = new ScanStats { FileCount = 1, DirectoryCount = 10_001 },
        };

        using var ms = new MemoryStream();
        TreeSerializer.Write(ms, result);
        ms.Position = 0;
        ScanResult loaded = TreeSerializer.Read(ms);

        cursor = loaded.Root;
        for (int i = 0; i < 10_000; i++) cursor = Assert.Single(cursor.Children!);
        Assert.Equal("leaf", Assert.Single(cursor.Children!).Name);
    }

    [Fact]
    public void Read_RejectsTruncatedSnapshot()
    {
        using var complete = new MemoryStream();
        TreeSerializer.Write(complete, MakeSample());
        byte[] truncated = complete.ToArray()[..^7];
        Assert.Throws<InvalidDataException>(() => TreeSerializer.Read(new MemoryStream(truncated)));
    }

    private static VolumeInfo SampleVolume() => new()
    {
        RootPath = "C:\\",
        DisplayName = "Test",
        FileSystemName = "NTFS",
        BytesPerCluster = 4096,
    };
}

public class SizeFormatterTests
{
    [Fact]
    public void FormatsBinaryUnitsLikeExplorer()
    {
        Assert.Equal("512 bytes", SizeFormatter.Format(512));
        Assert.Equal("1.5 KB", SizeFormatter.Format((ulong)(1.5 * 1024)));
        Assert.Equal("2 MB", SizeFormatter.Format(2UL * 1024 * 1024));
        Assert.Equal("1 GB", SizeFormatter.Format(1024UL * 1024 * 1024));
    }

    [Fact]
    public void ShortFormatOmitsDecimalsBelowGB()
    {
        Assert.Equal("970 MB", SizeFormatter.FormatShort(970UL * 1024 * 1024 + 12345));
        Assert.Equal("12 B", SizeFormatter.FormatShort(12));
    }
}

public class TreeOpsTests
{
    [Fact]
    public void Finalize_ComputesPostOrderTotalsAndSortsDescending()
    {
        var root = new FsNode { Name = "root", Flags = NodeFlags.Directory };
        var a = new FsNode { Name = "a", Flags = NodeFlags.Directory };
        a.AddChild(new FsNode { Name = "f1", LogicalSize = 10, AllocatedSize = 100 });
        a.AddChild(new FsNode { Name = "f2", LogicalSize = 20, AllocatedSize = 200 });
        var b = new FsNode { Name = "b", Flags = NodeFlags.Directory };
        b.AddChild(new FsNode { Name = "f3", LogicalSize = 30, AllocatedSize = 500 });
        var lonely = new FsNode { Name = "lonely.bin", LogicalSize = 7, AllocatedSize = 64 };

        root.AddChild(a);
        root.AddChild(b);
        root.AddChild(lonely);

        TreeOps.Finalize(root);

        Assert.Equal(300UL, a.TotalAllocated);
        Assert.Equal(500UL, b.TotalAllocated);
        Assert.Equal(864UL, root.TotalAllocated);
        Assert.Equal(67UL, root.TotalLogical);

        // Descending order: b (500), a (300), lonely.bin (64)
        Assert.Equal(new[] { "b", "a", "lonely.bin" }, root.Children!.Select(c => c.Name));
    }

    [Fact]
    public void GetPath_BuildsWindowsStyleFullPath()
    {
        var root = new FsNode { Name = "C:\\", Flags = NodeFlags.Directory };
        var dir = new FsNode { Name = "tools", Flags = NodeFlags.Directory };
        var file = new FsNode { Name = "app.exe" };
        root.AddChild(dir);
        dir.AddChild(file);

        Assert.Equal("C:\\tools\\app.exe", file.GetPath());
        Assert.Equal("C:\\tools\\", dir.GetPath());
        Assert.Equal("C:\\", root.GetPath());
    }
}

public class SunburstLayoutTests
{
    [Fact]
    public void Build_ProducesStableCompleteAndHittableLayout()
    {
        var root = new FsNode { Name = "root", Flags = NodeFlags.Directory };
        for (int i = 0; i < 20; i++)
            root.AddChild(new FsNode { Name = $"item-{i}", AllocatedSize = (ulong)(20 - i) * 1_000 });
        TreeOps.Finalize(root);

        SunburstNode first = SunburstLayout.Build(root, minSweepRadians: 0.08);
        SunburstNode second = SunburstLayout.Build(root, minSweepRadians: 0.08);

        Assert.Equal(SunburstLayout.FullCircle, first.Sweep, 10);
        Assert.Equal(first.Children![0].BranchIndex, second.Children![0].BranchIndex);
        Assert.NotNull(first.AggregatedWedge);
        SunburstNode child = first.Children[0];
        SunburstNode? hit = SunburstLayout.HitTest(first, 0.26, child.MidAngle, 0.16, 0.085, 0.012, 6);
        Assert.Same(child, hit);
    }
}

public class ScannerTests
{
    [Fact]
    public void NormalizeScanRoot_ResolvesRelativePaths()
    {
        string normalized = PathUtils.NormalizeScanRoot(".");
        Assert.True(Path.IsPathFullyQualified(normalized));
        Assert.Equal(Path.GetFullPath(".").TrimEnd('\\', '/'), normalized);
    }

    [Fact]
    public void GenericScanner_CountsDirectoriesOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), $"visdir_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "a", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.WriteAllText(Path.Combine(root, "root.txt"), "root");
        File.WriteAllText(Path.Combine(root, "a", "nested", "leaf.txt"), "leaf");
        try
        {
            ScanResult result = new GenericScanner().Scan(
                new ScanOptions { Path = root, Threads = 2 }, CancellationToken.None, null);
            Assert.Equal(2, result.Stats.FileCount);
            Assert.Equal(4, result.Stats.DirectoryCount);
            Assert.Equal(0, result.Stats.ErrorCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public unsafe void NtfsParser_RejectsBadUpdateSequenceFixup()
    {
        byte[] record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BitConverter.GetBytes((ushort)0x30).CopyTo(record, 4);
        BitConverter.GetBytes((ushort)3).CopyTo(record, 6);
        BitConverter.GetBytes((ushort)0x38).CopyTo(record, 0x14);
        BitConverter.GetBytes((uint)0x40).CopyTo(record, 0x18);
        BitConverter.GetBytes((ushort)0xAAAA).CopyTo(record, 0x30);
        fixed (byte* pointer = record)
        {
            Assert.False(NtfsRecordParser.TryParseRecord(pointer, record.Length, out _, out byte stage));
            Assert.Equal(NtfsRecordParser.FailFixup, stage);
        }
    }

    [Fact]
    public unsafe void NtfsParser_ResidentDataHasLogicalButNoSeparateAllocation()
    {
        byte[] record = MakeDataRecord(nonResident: false, valueOrAllocated: 137, lowestVcn: 0);
        fixed (byte* pointer = record)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(pointer, record.Length, out MftEntryInfo info));
            Assert.True(info.PrimaryDataResident);
            Assert.Equal(137UL, info.LogicalSize);
            Assert.Equal(0UL, info.DataAllocatedSize);
        }
    }

    [Fact]
    public unsafe void NtfsParser_IgnoresUndefinedSizesOnContinuationExtent()
    {
        byte[] record = MakeDataRecord(nonResident: true, valueOrAllocated: 0x1234_5000, lowestVcn: 42);
        fixed (byte* pointer = record)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(pointer, record.Length, out MftEntryInfo info));
            Assert.True(info.HasPrimaryData);
            Assert.Equal(0UL, info.LogicalSize);
            Assert.Equal(0UL, info.DataAllocatedSize);
        }
    }

    private static byte[] MakeDataRecord(bool nonResident, ulong valueOrAllocated, ulong lowestVcn)
    {
        byte[] record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BitConverter.GetBytes((ushort)0x30).CopyTo(record, 4); // USA offset
        BitConverter.GetBytes((ushort)3).CopyTo(record, 6);   // two sectors + sequence
        BitConverter.GetBytes((ushort)0x38).CopyTo(record, 0x14);
        BitConverter.GetBytes((ushort)0x0001).CopyTo(record, 0x16); // in use
        BitConverter.GetBytes((uint)123).CopyTo(record, 0x2C);

        const ushort sequence = 0xA55A;
        BitConverter.GetBytes(sequence).CopyTo(record, 0x30);
        BitConverter.GetBytes((ushort)0).CopyTo(record, 0x32);
        BitConverter.GetBytes((ushort)0).CopyTo(record, 0x34);
        BitConverter.GetBytes(sequence).CopyTo(record, 510);
        BitConverter.GetBytes(sequence).CopyTo(record, 1022);

        const int attr = 0x38;
        BitConverter.GetBytes(0x80u).CopyTo(record, attr); // $DATA
        record[attr + 8] = nonResident ? (byte)1 : (byte)0;

        int attrLength;
        if (nonResident)
        {
            attrLength = 0x48;
            BitConverter.GetBytes(lowestVcn).CopyTo(record, attr + 0x10);
            BitConverter.GetBytes(lowestVcn + 1).CopyTo(record, attr + 0x18);
            BitConverter.GetBytes(valueOrAllocated).CopyTo(record, attr + 0x28);
            BitConverter.GetBytes(valueOrAllocated / 2).CopyTo(record, attr + 0x30);
        }
        else
        {
            attrLength = 0xA8;
            BitConverter.GetBytes((uint)valueOrAllocated).CopyTo(record, attr + 0x10);
            BitConverter.GetBytes((ushort)0x18).CopyTo(record, attr + 0x14);
        }

        BitConverter.GetBytes((uint)attrLength).CopyTo(record, attr + 4);
        int end = attr + attrLength;
        BitConverter.GetBytes(0xFFFF_FFFFu).CopyTo(record, end);
        BitConverter.GetBytes((uint)(end + 8)).CopyTo(record, 0x18);
        return record;
    }

    [Theory]
    [InlineData(0UL, 4096U, 0UL)]
    [InlineData(1UL, 4096U, 4096UL)]
    [InlineData(4097UL, 4096U, 8192UL)]
    public void MftMath_RoundsToClusters(ulong value, uint cluster, ulong expected) =>
        Assert.Equal(expected, MftMath.RoundToCluster(value, cluster));
}

public class UpdateServiceTests
{
    [Fact]
    public void UpdateHelper_HasValidPowerShellSyntax()
    {
        if (!OperatingSystem.IsWindows()) return;
        string script = Path.Combine(Path.GetTempPath(), $"visdir_update_parser_{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(script, UpdateService.UpdateScriptForTests);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Environment["VISDIR_SCRIPT_TO_PARSE"] = script;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$tokens=$null;$errors=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($env:VISDIR_SCRIPT_TO_PARSE,[ref]$tokens,[ref]$errors);if($errors.Count){$errors|ForEach-Object{Write-Error $_};exit 1}");

            using Process process = Process.Start(startInfo)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(15_000), "PowerShell parser timed out.");
            Assert.True(process.ExitCode == 0, $"PowerShell syntax errors:{Environment.NewLine}{stdout}{stderr}");
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public void NormalizeDigest_AcceptsGitHubFormatAndRejectsMissingDigest()
    {
        string hex = new('a', 64);
        Assert.Equal(hex, UpdateService.NormalizeDigest("sha256:" + hex.ToUpperInvariant()));
        Assert.Throws<InvalidDataException>(() => UpdateService.NormalizeDigest(null));
    }

    [Fact]
    public void VerifyFileDigest_DetectsTampering()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "verified payload");
            string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            UpdateService.VerifyFileDigest(file, digest);
            File.AppendAllText(file, "tampered");
            Assert.Throws<InvalidDataException>(() => UpdateService.VerifyFileDigest(file, digest));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ExtractUpdateArchive_RejectsSiblingTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), $"visdir_update_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string zip = Path.Combine(root, "bad.zip");
        string staging = Path.Combine(root, "Staged");
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                archive.CreateEntry("../StagedEvil/payload.exe");
            Assert.Throws<InvalidDataException>(() => UpdateService.ExtractUpdateArchive(zip, staging));
            Assert.False(File.Exists(Path.Combine(root, "StagedEvil", "payload.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExtractUpdateArchive_RequiresAndExtractsExpectedExecutables()
    {
        string root = Path.Combine(Path.GetTempPath(), $"visdir_update_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string zip = Path.Combine(root, "good.zip");
        string staging = Path.Combine(root, "Staged");
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "VisDir.App.exe", "app");
                WriteEntry(archive, "VisDir.Scanner.dll", "scanner");
            }
            string extracted = UpdateService.ExtractUpdateArchive(zip, staging);
            Assert.Equal("app", File.ReadAllText(Path.Combine(extracted, "VisDir.App.exe")));
            Assert.Equal("scanner", File.ReadAllText(Path.Combine(extracted, "VisDir.Scanner.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExtractUpdateArchive_RejectsDuplicateAndAlternateDataStreamPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"visdir_update_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string duplicateZip = Path.Combine(root, "duplicate.zip");
            using (var archive = ZipFile.Open(duplicateZip, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "VisDir.App.exe", "first");
                WriteEntry(archive, "VisDir.App.exe", "second");
            }
            Assert.Throws<InvalidDataException>(() =>
                UpdateService.ExtractUpdateArchive(duplicateZip, Path.Combine(root, "DuplicateStaging")));

            string adsZip = Path.Combine(root, "ads.zip");
            using (var archive = ZipFile.Open(adsZip, ZipArchiveMode.Create))
                WriteEntry(archive, "VisDir.App.exe:payload", "unsafe");
            Assert.Throws<InvalidDataException>(() =>
                UpdateService.ExtractUpdateArchive(adsZip, Path.Combine(root, "AdsStaging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using Stream stream = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}

public class NtfsRecordParserTests
{
    private const ushort UsaOffset = 0x30;
    private const ushort UsaSequence = 0xA55A;

    // Minimal synthetic FILE record: header + appended attrs + end marker, closed by a
    // valid update sequence (sector tails stashed into the USA, as on disk). USA offset
    // is 0x30: NtfsRecordParser rejects anything below 0x30 with FailFixup.
    private sealed class RecordBuilder
    {
        public byte[] Buf;
        public int Cursor;
        public ushort UsaCount;

        public RecordBuilder(int size, ushort usaCount, ushort flags = 0x0001, uint recNo = 7, ulong baseRef = 0)
        {
            Buf = new byte[size];
            UsaCount = usaCount;
            "FILE"u8.CopyTo(Buf);
            BitConverter.GetBytes(UsaOffset).CopyTo(Buf, 4);
            BitConverter.GetBytes(usaCount).CopyTo(Buf, 6);
            int attrsOff = (UsaOffset + usaCount * 2 + 7) & ~7;
            BitConverter.GetBytes((ushort)attrsOff).CopyTo(Buf, 0x14);
            BitConverter.GetBytes(flags).CopyTo(Buf, 0x16);
            BitConverter.GetBytes(baseRef).CopyTo(Buf, 0x20);
            BitConverter.GetBytes(recNo).CopyTo(Buf, 0x2C);
            Cursor = attrsOff;
        }

        public void Append(byte[] attr)
        {
            attr.CopyTo(Buf, Cursor);
            Cursor += attr.Length;
        }

        public byte[] Finish()
        {
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(Buf, Cursor);
            Cursor += 8;
            BitConverter.GetBytes((uint)Cursor).CopyTo(Buf, 0x18);
            int sectors = UsaCount - 1;
            int stride = Buf.Length / sectors;
            for (int s = 1; s <= sectors; s++)
            {
                int slot = s * stride - 2;
                Buf[UsaOffset + s * 2] = Buf[slot];
                Buf[UsaOffset + s * 2 + 1] = Buf[slot + 1];
                Buf[slot] = (byte)(UsaSequence & 0xFF);
                Buf[slot + 1] = (byte)(UsaSequence >> 8);
            }
            BitConverter.GetBytes(UsaSequence).CopyTo(Buf, UsaOffset);
            return Buf;
        }
    }

    private static byte[] ResidentAttr(uint type, byte[] value, string? name = null)
    {
        int nameLen = name?.Length ?? 0;
        int valueOffset = 0x18 + nameLen * 2;
        int attrLen = (valueOffset + value.Length + 7) & ~7;
        var attr = new byte[attrLen];
        BitConverter.GetBytes(type).CopyTo(attr, 0);
        BitConverter.GetBytes((uint)attrLen).CopyTo(attr, 4);
        attr[8] = 0; // resident
        attr[9] = (byte)nameLen;
        BitConverter.GetBytes((ushort)0x18).CopyTo(attr, 0x0A);
        BitConverter.GetBytes((uint)value.Length).CopyTo(attr, 0x10);
        BitConverter.GetBytes((ushort)valueOffset).CopyTo(attr, 0x14);
        if (name is not null)
            for (int i = 0; i < name.Length; i++)
                BitConverter.GetBytes(name[i]).CopyTo(attr, 0x18 + i * 2);
        value.CopyTo(attr, valueOffset);
        return attr;
    }

    private static byte[] NonResidentDataAttr(ushort attrFlags, ulong alloc, ulong real, ulong compressed, ulong lowestVcn = 0, string? name = null)
    {
        int nameLen = name?.Length ?? 0;
        const int headerLen = 0x48;
        int nameOffset = headerLen;
        int runsOffset = headerLen + nameLen * 2;
        int attrLen = (runsOffset + 1 + 7) & ~7; // +1 zero run terminator
        var attr = new byte[attrLen];
        BitConverter.GetBytes(0x80u).CopyTo(attr, 0);
        BitConverter.GetBytes((uint)attrLen).CopyTo(attr, 4);
        attr[8] = 1; // non-resident
        attr[9] = (byte)nameLen;
        BitConverter.GetBytes((ushort)nameOffset).CopyTo(attr, 0x0A);
        BitConverter.GetBytes(attrFlags).CopyTo(attr, 0x0C);
        BitConverter.GetBytes(lowestVcn).CopyTo(attr, 0x10);
        BitConverter.GetBytes(lowestVcn).CopyTo(attr, 0x18); // highestVcn (unused by parser)
        BitConverter.GetBytes((ushort)runsOffset).CopyTo(attr, 0x20);
        BitConverter.GetBytes(alloc).CopyTo(attr, 0x28);
        BitConverter.GetBytes(real).CopyTo(attr, 0x30);
        BitConverter.GetBytes(real).CopyTo(attr, 0x38); // initialized size (unused by parser)
        BitConverter.GetBytes(compressed).CopyTo(attr, 0x40);
        if (name is not null)
            for (int i = 0; i < name.Length; i++)
                BitConverter.GetBytes(name[i]).CopyTo(attr, nameOffset + i * 2);
        attr[runsOffset] = 0;
        return attr;
    }

    private static byte[] FileNameValue(ulong parent, string name, byte ns, uint fileFlags = 0)
    {
        var value = new byte[0x42 + name.Length * 2];
        BitConverter.GetBytes(parent).CopyTo(value, 0);
        BitConverter.GetBytes(fileFlags).CopyTo(value, 0x38);
        value[0x40] = (byte)name.Length;
        value[0x41] = ns;
        for (int i = 0; i < name.Length; i++)
            BitConverter.GetBytes(name[i]).CopyTo(value, 0x42 + i * 2);
        return value;
    }

    private static byte[] FileNameAttr(ulong parent, string name, byte ns, uint fileFlags = 0) =>
        ResidentAttr(0x30, FileNameValue(parent, name, ns, fileFlags));

    private static byte[] SiAttr(uint fileFlags = 0)
    {
        var value = new byte[0x24];
        BitConverter.GetBytes(fileFlags).CopyTo(value, 0x20);
        return ResidentAttr(0x10, value);
    }

    private static byte[] ReparseAttr(uint tag)
    {
        var value = new byte[8];
        BitConverter.GetBytes(tag).CopyTo(value, 0);
        return ResidentAttr(0xC0, value);
    }

    private static byte[] ResidentDataAttr(uint valueLen, string? name = null) =>
        ResidentAttr(0x80, new byte[valueLen], name);

    private static byte[] IndexAllocAttr(ulong lowestVcn, ulong allocSize)
    {
        const int runsOffset = 0x40;
        const int attrLen = 0x48;
        var attr = new byte[attrLen];
        BitConverter.GetBytes(0xA0u).CopyTo(attr, 0);
        BitConverter.GetBytes((uint)attrLen).CopyTo(attr, 4);
        attr[8] = 1; // non-resident
        BitConverter.GetBytes(lowestVcn).CopyTo(attr, 0x10);
        BitConverter.GetBytes((ushort)runsOffset).CopyTo(attr, 0x20);
        BitConverter.GetBytes(allocSize).CopyTo(attr, 0x28);
        attr[runsOffset] = 0;
        return attr;
    }

    // valueOffset 0x30 + valueLength 0xFFFFFFF8 wraps to 0x28 in u32 math.
    private static byte[] OverflowAttr(uint type)
    {
        var attr = new byte[0x40];
        BitConverter.GetBytes(type).CopyTo(attr, 0);
        BitConverter.GetBytes((uint)0x40).CopyTo(attr, 4);
        attr[8] = 0; // resident
        BitConverter.GetBytes(0xFFFFFFF8u).CopyTo(attr, 0x10); // valueLength
        BitConverter.GetBytes((ushort)0x30).CopyTo(attr, 0x14); // valueOffset
        return attr;
    }

    private static byte[] TruncatedTailAttr()
    {
        var attr = new byte[0x10];
        BitConverter.GetBytes(0x80u).CopyTo(attr, 0);
        BitConverter.GetBytes((uint)0x400).CopyTo(attr, 4); // claims far beyond usedSize
        attr[8] = 1;
        return attr;
    }

    [Fact]
    public unsafe void SparseData_ReadsCompressedSizeAt0x40()
    {
        const ulong allocUncompressed = 200UL * 1024 * 1024 * 1024; // +0x28 decoy
        const ulong real = 12345; // +0x30
        const ulong compressed = 2UL * 1024 * 1024 * 1024; // +0x40
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(NonResidentDataAttr(attrFlags: 0x8000, alloc: allocUncompressed, real: real, compressed: compressed));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal(compressed, info.DataAllocatedSize);
            Assert.Equal(real, info.LogicalSize);
            Assert.True(info.HasPrimaryData);
            Assert.True(info.Sparse);
        }
    }

    [Fact]
    public unsafe void CompressedData_ReadsCompressedSizeAt0x40()
    {
        const ulong allocUncompressed = 200UL * 1024 * 1024 * 1024;
        const ulong real = 99999;
        const ulong compressed = 2UL * 1024 * 1024 * 1024;
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(NonResidentDataAttr(attrFlags: 0x0001, alloc: allocUncompressed, real: real, compressed: compressed));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal(compressed, info.DataAllocatedSize);
            Assert.Equal(real, info.LogicalSize);
            Assert.True(info.Compressed);
        }
    }

    [Fact]
    public unsafe void AdsSparse_Reads0x40()
    {
        const ulong allocUncompressed = 200UL * 1024 * 1024 * 1024;
        const ulong real = 777;
        const ulong compressed = 2UL * 1024 * 1024 * 1024;
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(NonResidentDataAttr(attrFlags: 0x8000, alloc: allocUncompressed, real: real, compressed: compressed, name: "ads"));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal(compressed, info.AdsAllocatedSize);
            Assert.Equal(0UL, info.DataAllocatedSize);
        }
    }

    [Fact]
    public unsafe void BoundsOverflow_FileNameGuarded()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(OverflowAttr(0x30));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.False(info.HasFileName);
        }
    }

    [Fact]
    public unsafe void BoundsOverflow_SI_Guarded()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(OverflowAttr(0x10));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.False(info.Offline);
        }
    }

    [Fact]
    public unsafe void Fixup_4KnStride()
    {
        var b1 = new RecordBuilder(1024, usaCount: 3); // 2 x 512B sectors
        b1.Append(FileNameAttr(parent: 5, name: "a", ns: 1));
        byte[] r1 = b1.Finish();
        var b2 = new RecordBuilder(4096, usaCount: 2); // 1 x 4096B sector (4Kn)
        b2.Append(FileNameAttr(parent: 5, name: "b", ns: 1));
        byte[] r2 = b2.Finish();
        fixed (byte* p1 = r1)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p1, r1.Length, out MftEntryInfo info1));
            Assert.Equal("a", info1.Name);
        }
        fixed (byte* p2 = r2)
        {
            // A 512-hardcoded stride would check phantom tails at 510/1022/... and fail fixup.
            Assert.True(NtfsRecordParser.TryParseRecord(p2, r2.Length, out MftEntryInfo info2));
            Assert.Equal("b", info2.Name);
        }
    }

    [Fact]
    public unsafe void HardlinkParent_Atomic()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(FileNameAttr(parent: 10, name: "alpha", ns: 1));
        b.Append(FileNameAttr(parent: 20, name: "beta", ns: 1));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal("alpha", info.Name);
            Assert.Equal(10UL, info.ParentRecordNumber);
            Assert.Equal(2, info.FileNameLinks);
        }
    }

    [Fact]
    public unsafe void DosName_NotCountedAsHardlink()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(FileNameAttr(parent: 5, name: "longname", ns: 1));
        b.Append(FileNameAttr(parent: 5, name: "LONGNA~1", ns: 2));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal(1, info.FileNameLinks);
            Assert.Equal("longname", info.Name);
        }
    }

    [Fact]
    public unsafe void Win32VsDos_Ranking()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(FileNameAttr(parent: 5, name: "SHORT~1", ns: 2));
        b.Append(FileNameAttr(parent: 5, name: "LongName", ns: 1));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal("LongName", info.Name);
            Assert.Equal(1, info.FileNameLinks);
        }
    }

    [Fact]
    public unsafe void ReparseTag_Parsed()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(FileNameAttr(parent: 5, name: "link", ns: 1));
        b.Append(ReparseAttr(0xA000000C));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal(0xA000000Cu, info.ReparseTag);
        }
    }

    [Fact]
    public unsafe void TornTail_NoName_Fails()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(TruncatedTailAttr());
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.False(NtfsRecordParser.TryParseRecord(p, rec.Length, out _, out byte stage));
            Assert.Equal(NtfsRecordParser.FailStructure, stage);
        }
    }

    [Fact]
    public unsafe void TornTail_WithName_KeepsName()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(FileNameAttr(parent: 5, name: "kept", ns: 1));
        b.Append(TruncatedTailAttr());
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal("kept", info.Name);
            Assert.True(info.HasFileName);
        }
    }

    [Fact]
    public unsafe void ResidentData_ExcludedFromAlloc()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(ResidentDataAttr(100));
        b.Append(ResidentDataAttr(50, name: "ads"));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.True(info.HasPrimaryData);
            Assert.True(info.PrimaryDataResident);
            Assert.Equal(100UL, info.LogicalSize);
            Assert.Equal(0UL, info.DataAllocatedSize);
            Assert.Equal(0UL, info.AdsAllocatedSize);
        }
    }

    [Fact]
    public unsafe void IndexAllocation_FirstExtentOnly()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(IndexAllocAttr(lowestVcn: 0, allocSize: 8192));
        b.Append(IndexAllocAttr(lowestVcn: 5, allocSize: 8192));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.Equal(8192UL, info.IndexAllocationSize);
        }
    }

    [Fact]
    public unsafe void ContinuationRecord_MarksHasPrimaryWithoutSizes()
    {
        var b = new RecordBuilder(1024, usaCount: 3);
        b.Append(NonResidentDataAttr(attrFlags: 0x0001, alloc: 0x1234_5000, real: 0x1234_5000, compressed: 0x1234_5000, lowestVcn: 42));
        byte[] rec = b.Finish();
        fixed (byte* p = rec)
        {
            Assert.True(NtfsRecordParser.TryParseRecord(p, rec.Length, out MftEntryInfo info));
            Assert.True(info.HasPrimaryData);
            Assert.Equal(0UL, info.LogicalSize);
            Assert.Equal(0UL, info.DataAllocatedSize);
            Assert.True(info.Compressed);
        }
    }
}
