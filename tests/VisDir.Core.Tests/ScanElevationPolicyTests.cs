using VisDir.Core;

namespace VisDir.Core.Tests;

public sealed class ScanElevationPolicyTests
{
    [Theory]
    [InlineData("auto", @"C:\", "NTFS", false, true)]
    [InlineData("auto", @"C:\", "NTFS", true, false)]
    [InlineData("generic", @"C:\", "NTFS", false, false)]
    [InlineData("auto", @"D:\", "exFAT", false, false)]
    [InlineData("auto", @"C:\Users", "NTFS", false, false)]
    [InlineData("auto", @"\\server\share", "NTFS", false, false)]
    [InlineData("mft", @"C:\Users", "NTFS", false, true)]
    public void RequestsElevationForAutomaticNtfsDrivesAndExplicitProtectedScans(
        string mode, string path, string filesystem, bool elevated, bool expected)
    {
        var coordinator = new ScanCoordinator(
            _ => new VolumeInfo { RootPath = path, DisplayName = "Test", FileSystemName = filesystem },
            () => elevated, _ => throw new InvalidOperationException("Policy must not start a scanner."));
        Assert.Equal(expected, coordinator.ShouldRequestElevation(mode, path));
    }
}
