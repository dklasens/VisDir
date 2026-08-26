namespace VisDir.Core;

public sealed record VolumeInfo
{
    public required string RootPath { get; init; }
    public required string DisplayName { get; init; }
    public string FileSystemName { get; init; } = "";
    public ulong VolumeSerialNumber { get; init; }
    public uint BytesPerCluster { get; init; } = 4096;
    public ulong TotalBytes { get; init; }
    public ulong FreeBytes { get; init; }
}
