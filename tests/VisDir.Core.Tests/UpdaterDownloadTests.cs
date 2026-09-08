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

    [Fact]
    public async Task FileLockRetry_BeatsRealHeldReadHandle()
    {
        // Simulates an antivirus/indexer holding the finished file: exclusive open
        // fails until the holder releases, then the retry envelope must succeed.
        string path = Path.Combine(Path.GetTempPath(), $"visdir-locktest-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "payload");
        try
        {
            using var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var releaser = Task.Run(async () =>
            {
                await Task.Delay(800);
                locker.Dispose();
            });
            using var winner = await UpdateService.WithFileLockRetryAsync(
                () => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None),
                CancellationToken.None);
            await releaser;
            Assert.True(winner.CanWrite);
        }
        finally
        {
            UpdateService.TryDeleteFileWithRetry(path);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task TryDeleteFileWithRetry_RemovesAfterRelease()
    {
        string path = Path.Combine(Path.GetTempPath(), $"visdir-deltest-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "payload");
        var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var releaser = Task.Run(async () =>
        {
            await Task.Delay(300);
            locker.Dispose();
        });
        UpdateService.TryDeleteFileWithRetry(path);
        await releaser;
        Assert.False(File.Exists(path));
    }
}
