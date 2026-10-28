using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using VisDir.Core;

namespace VisDir.Core.Tests;

public sealed class GenericAccuracyTests
{
    private unsafe delegate int ParseBatch(byte* data, int length, List<FsNode> sink, bool extended, ulong volume);

    [Fact]
    public unsafe void EnumerationPreservesFullIdentityAndDoesNotDeduplicateDistinctFiles()
    {
        using var scanner = new GenericScanner();
        var parse = typeof(GenericScanner).GetMethod("ParseBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<ParseBatch>(scanner);
        var children = new List<FsNode>();
        foreach (ulong high in new ulong[] { 1, 2 })
        {
            byte[] entry = Entry(high == 1 ? "a" : "b", 42, high);
            fixed (byte* buffer = entry) Assert.Equal(1, parse(buffer, entry.Length, children, true, 0x12345678ABCDEFUL));
        }
        var root = new FsNode { Name = @"C:\", Flags = NodeFlags.Directory };
        root.AddChildren(children);
        HardlinkAccounting.Apply(root);
        TreeOps.Finalize(root);
        Assert.Equal(8192UL, root.TotalAllocated);
        Assert.Equal(2UL, children[1].FileKeyHigh);
        Assert.Equal(0x12345678ABCDEFUL, children[1].VolumeKey);
    }

    [Fact]
    public unsafe void MalformedNamesCannotReadIntoNextEntry()
    {
        using var scanner = new GenericScanner();
        var parse = typeof(GenericScanner).GetMethod("ParseBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<ParseBatch>(scanner);
        byte[] data = new byte[192];
        Entry("a", 42, 0).CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data, 96);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60), 32);
        var children = new List<FsNode>();
        fixed (byte* buffer = data) Assert.Equal(-1, parse(buffer, data.Length, children, true, 1));
        Assert.Empty(children);
    }

    [Fact]
    public void ActualHardlinksAreStableAcrossThreadCountsAndSubfolderScopes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "visdir-hardlinks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "a"));
        Directory.CreateDirectory(Path.Combine(directory, "z"));
        string source = Path.Combine(directory, "z", "original.bin");
        string alias = Path.Combine(directory, "a", "alias.bin");
        File.WriteAllBytes(source, new byte[16 * 1024]);
        try
        {
            Assert.True(CreateHardLinkW(alias, source, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
            using var scanner = new GenericScanner();
            ScanResult first = scanner.Scan(new ScanOptions { Path = directory, Threads = 1 }, default, null);
            ScanResult second = scanner.Scan(new ScanOptions { Path = directory, Threads = 8 }, default, null);
            Assert.Equal(2, first.Stats.FileCount);
            Assert.Equal(first.Root.TotalAllocated, second.Root.TotalAllocated);
            Assert.Equal(0UL, first.Root.Children!.Single(n => n.Name == "z").TotalAllocated);
            Assert.True(first.Root.Children!.Single(n => n.Name == "a").TotalAllocated > 0);
            ScanResult subtree = scanner.Scan(new ScanOptions { Path = Path.Combine(directory, "z") }, default, null);
            Assert.Equal(first.Root.TotalAllocated, subtree.Root.TotalAllocated);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CancelledScanDoesNotPublishAPartialSuccess()
    {
        using var scanner = new GenericScanner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => scanner.Scan(
            new ScanOptions { Path = Path.GetTempPath() }, cancellation.Token, null));
    }

    private static byte[] Entry(string name, long low, ulong high)
    {
        var bytes = new byte[96];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), 4096);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), (uint)(name.Length * 2));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(72), low);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), high);
        Encoding.Unicode.GetBytes(name).CopyTo(bytes, 88);
        return bytes;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
}
