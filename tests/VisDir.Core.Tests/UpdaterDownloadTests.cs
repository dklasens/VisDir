using VisDir.App.Update;
using Xunit;

namespace VisDir.Core.Tests;

/// <summary>Regression tests for the v1.2.0 updater sharing-violation failure:
/// concurrent downloads shared one exclusively-locked partial path.</summary>
public class UpdaterDownloadPathTests
{
    [Fact]
    public void PartialPaths_AreUniquePerAttempt()
    {
        string zip = Path.Combine(Path.GetTempPath(), "VisDir-9.9.9.zip");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 25; i++)
            Assert.True(seen.Add(UpdateService.NewUniquePartialPath(zip)), "partial paths must never repeat");
    }

    [Fact]
    public void PartialPath_StaysBesideFinalZipWithPartialSuffix()
    {
        string zip = Path.Combine(Path.GetTempPath(), "VisDir-1.2.1.zip");
        string partial = UpdateService.NewUniquePartialPath(zip);
        Assert.StartsWith(zip + ".", partial, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".partial", partial, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetDirectoryName(zip), Path.GetDirectoryName(partial));
    }

    [Fact]
    public async Task FileLockRetry_SucceedsAfterTransientSharingViolation()
    {
        int calls = 0;
        string result = await UpdateService.WithFileLockRetryAsync(() =>
        {
            if (++calls < 3)
                throw new IOException("locked", unchecked((int)0x80070020));
            return "ok";
        }, CancellationToken.None);
        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task FileLockRetry_PassesThroughNonLockErrors()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateService.WithFileLockRetryAsync<string>(
                () => throw new InvalidDataException("nope"), CancellationToken.None));
    }
}
