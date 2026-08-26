using System.Diagnostics;
using VisDir.Core;

namespace VisDir.Benchmarks;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (TryArgument(args, "--scan", out string? scanPath)) return RunScan(scanPath!);
        int nodes = TryArgument(args, "--nodes", out string? raw) && int.TryParse(raw, out int parsed)
            ? Math.Clamp(parsed, 1_000, 5_000_000)
            : 250_000;
        return RunSynthetic(nodes);
    }

    private static int RunSynthetic(int nodeCount)
    {
        Console.WriteLine($"VisDir synthetic benchmark · {nodeCount:N0} nodes");
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        FsNode root = BuildTree(nodeCount);
        clock.Stop();
        Print("construct", nodeCount, clock.Elapsed, 0);

        clock.Restart();
        TreeOps.Finalize(root);
        clock.Stop();
        Print("finalize", nodeCount, clock.Elapsed, 0);

        var result = new ScanResult
        {
            Root = root,
            Volume = new VolumeInfo { RootPath = "C:\\", DisplayName = "Benchmark", FileSystemName = "NTFS" },
            EngineName = "benchmark",
            Stats = new ScanStats { FileCount = nodeCount, DirectoryCount = 1 },
        };
        using var snapshot = new MemoryStream(capacity: Math.Min(nodeCount * 80, 1_000_000_000));
        clock.Restart();
        TreeSerializer.Write(snapshot, result);
        clock.Stop();
        Print("serialize", nodeCount, clock.Elapsed, snapshot.Length);

        snapshot.Position = 0;
        clock.Restart();
        ScanResult loaded = TreeSerializer.Read(snapshot);
        clock.Stop();
        Print("deserialize", nodeCount, clock.Elapsed, snapshot.Length);
        GC.KeepAlive(loaded);

        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        Console.WriteLine($"managed allocated: {SizeFormatter.Format((ulong)allocated)}");
        return 0;
    }

    private static FsNode BuildTree(int target)
    {
        var root = new FsNode { Name = "benchmark", Flags = NodeFlags.Directory };
        var directories = new List<FsNode> { root };
        for (int i = 1; i < target; i++)
        {
            FsNode parent = directories[(i / 64) % directories.Count];
            if (i % 16 == 0)
            {
                var directory = new FsNode { Name = $"dir-{i}", Flags = NodeFlags.Directory };
                parent.AddChild(directory);
                directories.Add(directory);
            }
            else
            {
                parent.AddChild(new FsNode
                {
                    Name = $"file-{i}.bin", LogicalSize = (ulong)(i % 1_000_000),
                    AllocatedSize = (ulong)((i % 256) + 1) * 4096,
                });
            }
        }
        return root;
    }

    private static int RunScan(string path)
    {
        path = Path.GetFullPath(path);
        Console.WriteLine($"VisDir compatible scanner benchmark · {path}");
        var progress = new InlineProgress<ScanProgress>(p =>
            Console.Write($"\r{p.FilesSeen:N0} files · {p.DirsSeen:N0} folders · {p.ElapsedMs / 1000:0.0}s"));
        ScanResult result = new GenericScanner().Scan(
            new ScanOptions { Path = path }, CancellationToken.None, progress);
        Console.WriteLine();
        Console.WriteLine($"{result.Stats.FileCount:N0} files · {SizeFormatter.Format(result.Root.TotalAllocated)} · {result.Stats.ElapsedMs / 1000:0.000}s");
        return 0;
    }

    private static void Print(string operation, int nodes, TimeSpan elapsed, long bytes)
    {
        double throughput = nodes / Math.Max(elapsed.TotalSeconds, 0.000_001);
        string suffix = bytes > 0 ? $" · {SizeFormatter.Format((ulong)bytes)} snapshot" : "";
        Console.WriteLine($"{operation,-12} {elapsed.TotalMilliseconds,9:N1} ms · {throughput,13:N0} nodes/s{suffix}");
    }

    private static bool TryArgument(string[] args, string name, out string? value)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        return value is not null;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
