using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace VisDir.App.Update;

public sealed record ReleaseInfo(
    string TagName,
    Version Version,
    string ReleaseNotes,
    string DownloadUrl,
    long AssetSizeBytes,
    string Sha256,
    string HtmlUrl
);

public sealed partial class UpdateService
{
    private const string RepoOwner = "dklasens";
    private const string RepoName = "VisDir";
    private const long MaxDownloadBytes = 1L << 30;
    private const long MaxExtractedBytes = 4L << 30;
    private const int MaxArchiveEntries = 25_000;
    private const string HealthMarkerEnvironmentVariable = "VISDIR_UPDATE_HEALTH_MARKER";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VisDir-App", GetCurrentVersion().ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is not null ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
    }

    /// <summary>Returns null only when installed version is current; update-check failures propagate.</summary>
    public async Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubReleaseDto>(json)
            ?? throw new InvalidDataException("GitHub returned an empty release document.");
        if (string.IsNullOrWhiteSpace(release.TagName))
            throw new InvalidDataException("The latest GitHub release has no version tag.");

        string versionStr = release.TagName.TrimStart('v', 'V');
        if (!Version.TryParse(versionStr, out Version? releaseVersion) &&
            !Version.TryParse(versionStr + ".0", out releaseVersion))
            throw new InvalidDataException($"The release tag '{release.TagName}' is not a valid version.");
        if (releaseVersion <= GetCurrentVersion()) return null;

        string targetAsset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "VisDir-win-arm64.zip"
            : "VisDir-win-x64.zip";
        GitHubAssetDto? asset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, targetAsset, StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            throw new InvalidDataException($"Release {release.TagName} does not contain {targetAsset}.");
        if (asset.Size is <= 0 or > MaxDownloadBytes)
            throw new InvalidDataException($"Release asset size {asset.Size:N0} is outside the accepted range.");

        string sha256;
        try
        {
            sha256 = NormalizeDigest(asset.Digest);
        }
        catch (InvalidDataException)
        {
            // Second channel: the release also publishes checksums-<rid>.sha256 manifests.
            string? fromManifest = await TryGetDigestFromChecksumsAssetAsync(
                release.Assets, targetAsset, ct).ConfigureAwait(false);
            sha256 = fromManifest ?? throw new InvalidDataException(
                $"Release {release.TagName} provides no SHA-256 digest for '{targetAsset}': " +
                "the asset digest is missing and no published checksums manifest supplied one. " +
                "The update was refused.");
        }

        return new ReleaseInfo(
            release.TagName,
            releaseVersion,
            release.Body ?? "No release notes provided.",
            asset.BrowserDownloadUrl,
            asset.Size,
            sha256,
            release.HtmlUrl ?? $"https://github.com/{RepoOwner}/{RepoName}/releases");
    }

    /// <summary>
    /// Downloads a published checksums manifest from the same release and extracts the digest
    /// for <paramref name="targetAsset"/>. Returns null when unavailable; the caller fails closed.
    /// </summary>
    private static async Task<string?> TryGetDigestFromChecksumsAssetAsync(
        List<GitHubAssetDto>? assets, string targetAsset, CancellationToken ct)
    {
        try
        {
            GitHubAssetDto? manifest = assets?.FirstOrDefault(a =>
                a.Name is not null &&
                a.Name.StartsWith("checksums-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
            if (manifest is null) return null;
            using var response = await HttpClient.GetAsync(manifest.BrowserDownloadUrl!, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length is 0 or > (1 << 20)) return null;
            string? digest = TryParseChecksumsManifest(Encoding.UTF8.GetString(bytes), targetAsset);
            return digest is null ? null : NormalizeDigest(digest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a SHA-256 manifest line ("&lt;64-hex&gt;  &lt;relative-path&gt;", hash anchored left so
    /// file names may contain spaces) and returns the digest for <paramref name="targetFileName"/>, if listed.
    /// </summary>
    internal static string? TryParseChecksumsManifest(string manifestText, string targetFileName)
    {
        string wantedBase = Path.GetFileName(targetFileName);
        foreach (string rawLine in manifestText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length < 66 || line.StartsWith('#')) continue;
            string hash = line[..64];
            if (!hash.All(Uri.IsHexDigit) || !char.IsWhiteSpace(line[64])) continue;
            string name = line[65..].TrimStart().TrimStart('*').Replace('/', Path.DirectorySeparatorChar);
            string listedBase = Path.GetFileName(name);
            if (name.Equals(targetFileName, StringComparison.OrdinalIgnoreCase) ||
                listedBase.Equals(wantedBase, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    public async Task<string> DownloadUpdateAsync(
        ReleaseInfo release,
        IProgress<(long BytesDownloaded, long TotalBytes, double Fraction)>? progress = null,
        CancellationToken ct = default)
    {
        string tempDir = EnsureSecureTempRoot();
        CleanupStalePartials(tempDir);
        string zipPath = Path.Combine(tempDir, $"VisDir-{release.Version}.zip");
        // Unique per attempt: a second writer (another instance, a reopened dialog,
        // a cancel-then-retry) must never share an exclusively-locked path.
        string partialPath = NewUniquePartialPath(zipPath);

        try
        {
            using var response = await HttpClient.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? release.AssetSizeBytes;
            if (totalBytes is <= 0 or > MaxDownloadBytes)
                throw new InvalidDataException($"Update download size {totalBytes:N0} is outside the accepted range.");

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = await WithFileLockRetryAsync(
                () => new FileStream(
                    partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan),
                ct).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long totalRead = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                totalRead = checked(totalRead + read);
                if (totalRead > MaxDownloadBytes)
                    throw new InvalidDataException("The update exceeded the maximum accepted download size.");
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                double fraction = totalBytes > 0 ? (double)totalRead / totalBytes : 0;
                progress?.Report((totalRead, totalBytes, Math.Min(1.0, fraction)));
            }

            await fileStream.FlushAsync(ct).ConfigureAwait(false);
            if (release.AssetSizeBytes > 0 && totalRead != release.AssetSizeBytes)
                throw new InvalidDataException(
                    $"Update size mismatch: expected {release.AssetSizeBytes:N0} bytes, received {totalRead:N0}.");
            string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(release.Sha256)))
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");

            await WithFileLockRetryAsync(() => { File.Move(partialPath, zipPath, overwrite: true); return true; }, ct).ConfigureAwait(false);
            return zipPath;
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }
    }

    public static void ApplyUpdateAndRestart(string zipPath, string expectedSha256)
    {
        VerifyFileDigest(zipPath, expectedSha256);
        string stagedDir = ExtractUpdateArchive(zipPath);
        try
        {
            // Second channel: staged payload must match its published checksums manifest,
            // and staged binaries must carry a trusted Authenticode signature. Fail closed.
            VerifyStagedChecksums(stagedDir);
            VerifyStagedSignatures(stagedDir);
        }
        catch
        {
            try { Directory.Delete(stagedDir, recursive: true); } catch { }
            throw;
        }
        string appDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        string currentExe = Path.GetFullPath(Environment.ProcessPath ?? Path.Combine(appDir, "VisDir.App.exe"));
        string relativeExe = Path.GetRelativePath(appDir, currentExe);
        if (relativeExe.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeExe))
            throw new InvalidOperationException("The running executable is outside the application directory.");

        string tempRoot = EnsureSecureTempRoot();
        string operationId = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(tempRoot, $"apply-update-{operationId}.ps1");
        string planPath = Path.Combine(tempRoot, $"apply-update-{operationId}.json");
        string markerPath = Path.Combine(tempRoot, $"healthy-{operationId}.marker");
        var plan = new UpdatePlan(
            appDir, stagedDir, relativeExe, Environment.ProcessId, operationId, markerPath,
            ExpectedSignerThumbprint);
        ValidateUpdatePlan(plan, appDir);
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        File.WriteAllText(scriptPath, UpdateScript);

        // Absolute System32 host: no PATH lookup, no planted powershell.exe.
        string powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
            throw new InvalidOperationException("Windows PowerShell is unavailable; the update was refused.");
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in new[]
                 {
                     "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass",
                     "-File", scriptPath, "-PlanPath", planPath,
                 })
            startInfo.ArgumentList.Add(argument);

        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("The update helper could not be started.");
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Validates an update plan before it is handed to the elevated helper: the target must be
    /// the install directory, staging and the health marker must stay under the update temp root,
    /// and the executable must be a relative in-staging path that exists.
    /// </summary>
    internal static void ValidateUpdatePlan(UpdatePlan plan, string expectedAppDir)
    {
        string tempPrefix = TempRootPrefix();
        string appRoot = Path.GetFullPath(expectedAppDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string planApp = Path.GetFullPath(plan.AppDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!planApp.Equals(appRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The update plan targets a directory outside the installation.");
        if (!IsUnderRoot(plan.StagedDir, tempPrefix))
            throw new InvalidOperationException("The update staging directory is outside the update temp root.");
        if (!IsUnderRoot(plan.MarkerPath, tempPrefix))
            throw new InvalidOperationException("The update health marker is outside the update temp root.");
        if (Path.IsPathRooted(plan.RelativeExe) ||
            plan.RelativeExe.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
            throw new InvalidOperationException("The update plan has an unsafe executable path.");
        string stagedExe = Path.GetFullPath(Path.Combine(Path.GetFullPath(plan.StagedDir), plan.RelativeExe));
        if (!IsUnderRoot(stagedExe, StagedPrefix(plan.StagedDir)) || !File.Exists(stagedExe))
            throw new InvalidOperationException("The update plan executable is missing from staging.");
    }

    internal static string StagedPrefix(string stagedDir) =>
        Path.GetFullPath(stagedDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    internal static string TempRootPrefix() =>
        Path.GetFullPath(GetUpdateTempRoot()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    internal static bool IsUnderRoot(string path, string rootPrefix) =>
        Path.GetFullPath(path).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    public static void ReportHealthyStart()
    {
        string? marker = Environment.GetEnvironmentVariable(HealthMarkerEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(marker)) return;
        try
        {
            // Constrain the marker to the update temp root: a tampered environment value
            // must not turn the helper into an arbitrary-file writer. Fail closed.
            string fullPath = Path.GetFullPath(marker);
            if (!IsUnderRoot(fullPath, TempRootPrefix())) return;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // A failed marker causes the external helper to roll back this update.
        }
    }
    internal static string ExtractUpdateArchive(string zipPath, string? destination = null)
    {
        string staging = Path.GetFullPath(destination ??
            Path.Combine(GetUpdateTempRoot(), $"staged-{Guid.NewGuid():N}"));
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        string stagingPrefix = staging.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException("The update archive contains too many entries.");
            long extractedBytes = 0;
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byte[] buffer = new byte[128 * 1024];
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string entryName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(entryName) ||
                    entryName.Split(Path.DirectorySeparatorChar).Any(part => part.Contains(':')))
                    throw new InvalidDataException($"Unsafe update entry: {entry.FullName}");

                string destPath = Path.GetFullPath(Path.Combine(staging, entryName));
                if (!destPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Update entry escapes staging: {entry.FullName}");
                if (!destinations.Add(destPath))
                    throw new InvalidDataException($"Update archive contains a duplicate path: {entry.FullName}");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using Stream source = entry.Open();
                using var destinationFile = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long entryBytes = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryBytes = checked(entryBytes + read);
                    extractedBytes = checked(extractedBytes + read);
                    if (extractedBytes > MaxExtractedBytes)
                        throw new InvalidDataException("The expanded update exceeds the accepted size limit.");
                    destinationFile.Write(buffer, 0, read);
                }
                if (entryBytes != entry.Length)
                    throw new InvalidDataException($"Update entry size mismatch: {entry.FullName}");
            }

            if (!File.Exists(Path.Combine(staging, "VisDir.App.exe")) ||
                !File.Exists(Path.Combine(staging, "VisDir.Scanner.dll")))
                throw new InvalidDataException("The update is missing required VisDir executables.");
            return staging;
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    internal static void VerifyFileDigest(string path, string expectedSha256)
    {
        string normalized = NormalizeDigest(expectedSha256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.SequentialScan);
        byte[] actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(normalized)))
            throw new InvalidDataException("The update archive failed SHA-256 verification.");
    }

    internal static string NormalizeDigest(string? digest)
    {
        string value = digest?.Trim() ?? "";
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("The release asset does not provide a valid SHA-256 digest.");
        return value.ToLowerInvariant();
    }

    private static string GetUpdateTempRoot() => Path.Combine(Path.GetTempPath(), "VisDir", "Updates");

    /// <summary>
    /// Creates the per-user update temp root with a current-user-only ACL so staged payloads,
    /// plans, and scripts cannot be pre-created or swapped by another local user.
    /// </summary>
    internal static string EnsureSecureTempRoot()
    {
        string tempRoot = GetUpdateTempRoot();
        Directory.CreateDirectory(tempRoot);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var owner = WindowsIdentity.GetCurrent().User;
                if (owner is not null)
                {
                    var security = new DirectorySecurity();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(
                        owner,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    new DirectoryInfo(tempRoot).SetAccessControl(security);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The directory already exists with a restrictive ACL we cannot rewrite;
                // that is the safe direction, so continue.
            }
        }
        return tempRoot;
    }

    /// <summary>
    /// Verifies every staged file against the published checksums manifest extracted with the
    /// payload. The manifest is the second integrity channel beside the zip digest; a missing
    /// manifest or an uncovered required binary fails closed.
    /// </summary>
    internal static void VerifyStagedChecksums(string stagedDir)
    {
        string[] manifests = Directory.GetFiles(stagedDir, "checksums-*.sha256", SearchOption.TopDirectoryOnly);
        if (manifests.Length == 0)
            throw new InvalidDataException(
                "The update is missing its checksums manifest; the update was refused.");
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifest in manifests.Order(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string rawLine in File.ReadLines(manifest))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.Length < 66 || !line[..64].All(Uri.IsHexDigit) || !char.IsWhiteSpace(line[64]))
                    throw new InvalidDataException(
                        $"The update checksums manifest '{Path.GetFileName(manifest)}' has an invalid line; " +
                        "the update was refused.");
                string hash = line[..64].ToLowerInvariant();
                string relative = line[65..].TrimStart().TrimStart('*').Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) ||
                    relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
                    throw new InvalidDataException($"Unsafe checksums entry '{relative}'; the update was refused.");
                string full = Path.GetFullPath(Path.Combine(stagedDir, relative));
                if (!IsUnderRoot(full, StagedPrefix(stagedDir)))
                    throw new InvalidDataException($"Update entry escapes staging: {relative}");
                if (!File.Exists(full))
                    throw new InvalidDataException(
                        $"The update is missing file '{relative}' listed in its checksums manifest; " +
                        "the update was refused.");
                VerifyFileDigest(full, hash);
                covered.Add(Path.GetFileName(relative));
            }
        }
        foreach (string required in new[] { "VisDir.App.exe", "VisDir.Scanner.dll" })
        {
            if (!covered.Contains(required))
                throw new InvalidDataException(
                    $"The update checksums manifest does not cover required binary '{required}'; " +
                    "the update was refused.");
        }
    }

    /// <summary>
    /// Pinned Authenticode signer thumbprint (hex, case/whitespace-insensitive) that staged update
    /// binaries must chain to. Set at release-signing time; when empty a valid OS trust chain is
    /// still required. Unsigned or untrusted binaries always fail.
    /// </summary>
    internal static string ExpectedSignerThumbprint { get; set; } = string.Empty;

    internal static void VerifyStagedSignatures(string stagedDir)
    {
        VerifyAuthenticodeSignature(Path.Combine(stagedDir, "VisDir.App.exe"));
        VerifyAuthenticodeSignature(Path.Combine(stagedDir, "VisDir.Scanner.dll"));
    }

    internal static void VerifyAuthenticodeSignature(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Authenticode verification requires Windows; the update was refused.");
        if (!File.Exists(path))
            throw new InvalidDataException(
                $"The update is missing required binary '{Path.GetFileName(path)}'; the update was refused.");
        int hr = WinVerifyTrustEmbedded(path);
        if (hr != 0)
            throw new InvalidDataException(
                $"The update binary '{Path.GetFileName(path)}' failed Authenticode trust verification " +
                $"(0x{hr:X8}); the update was refused.");
        string expected = ExpectedSignerThumbprint.Replace(" ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("\t", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        if (expected.Length == 0) return;
        string actual;
        try
        {
            // Authenticode signer extraction has no non-obsolete replacement in .NET 10
            // (dotnet/runtime#113552); scoped suppression on this Windows-only path.
#pragma warning disable SYSLIB0057 // Type or member is obsolete
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            actual = certificate.Thumbprint.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"The update binary '{Path.GetFileName(path)}' has no readable signer certificate; " +
                "the update was refused.", ex);
        }
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The update binary '{Path.GetFileName(path)}' was signed by an unexpected publisher; " +
                "the update was refused.");
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public nint HFile;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint CbStruct;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice; // WTD_UI_NONE = 2
        public uint RevocationChecks; // WTD_REVOKE_NONE = 0 (offline-safe; chain trust still enforced)
        public uint UnionChoice; // WTD_CHOICE_FILE = 1
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
    }

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WinVerifyTrust(nint hwnd, in Guid actionId, in WinTrustData data);

    private static int WinVerifyTrustEmbedded(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
            HFile = 0,
            KnownSubject = 0,
        };
        nint pFileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFileInfo, fDeleteOld: false);
            var data = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = pFileInfo,
            };
            return WinVerifyTrust(0, in WinTrustActionGenericVerifyV2, in data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFileInfo);
            Marshal.FreeCoTaskMem(pFileInfo);
        }
    }

    internal sealed record UpdatePlan(
        string AppDir, string StagedDir, string RelativeExe, int ProcessId, string OperationId, string MarkerPath,
        string ExpectedSignerThumbprint);

    /// <summary>Derives a fresh partial-download path beside <paramref name="zipPath"/>.</summary>
    internal static string NewUniquePartialPath(string zipPath) =>
        $"{zipPath}.{Guid.NewGuid():N}.partial";

    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private static bool IsSharingViolation(IOException ex) => ex.HResult == SharingViolationHResult;

    /// <summary>Retries file open/move across transient locks (second instance, AV/indexer).</summary>
    internal static async Task<T> WithFileLockRetryAsync<T>(Func<T> open, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return open();
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt + 1 < maxAttempts)
            {
                await Task.Delay(250 << attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Best-effort removal of partials orphaned by killed runs (legacy shared names too).</summary>
    private static void CleanupStalePartials(string tempDir)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(2);
            foreach (string f in Directory.EnumerateFiles(tempDir, "VisDir-*.partial*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff) TryDeleteFile(f);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal static string UpdateScriptForTests => UpdateScript;

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }

    private const string UpdateScript = """
param([Parameter(Mandatory = $true)][string] $PlanPath, [switch] $Elevated)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
$logPath = Join-Path ([System.IO.Path]::GetTempPath()) 'VisDir\Updates\apply-update.log'

function Write-UpdateLog([string] $Message) {
    Add-Content -LiteralPath $logPath -Value ("{0:o} {1}" -f [DateTimeOffset]::UtcNow, $Message)
}
function Start-InstalledApp([string] $Root) {
    $exe = Join-Path $Root $plan.RelativeExe
    if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe | Out-Null }
}
function Test-UpdatePlan {
    $tempRoot = ([System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) 'VisDir\Updates')).TrimEnd('\', '/') + '\')
    $stagedDir = [System.IO.Path]::GetFullPath([string]$plan.StagedDir)
    $marker = [System.IO.Path]::GetFullPath([string]$plan.MarkerPath)
    foreach ($p in @($stagedDir, $marker)) {
        if (-not $p.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Update plan escapes the update temp root.'
        }
    }
    $rel = [string]$plan.RelativeExe
    if ([System.IO.Path]::IsPathRooted($rel) -or ($rel -split '[\\/]' -contains '..')) {
        throw 'Update plan has an unsafe executable path.'
    }
    $stagedExe = [System.IO.Path]::GetFullPath((Join-Path $stagedDir $rel))
    if (-not $stagedExe.StartsWith(($stagedDir.TrimEnd('\', '/') + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Update plan executable escapes staging.'
    }
    if (-not (Test-Path -LiteralPath $stagedExe)) { throw 'Update plan executable is missing from staging.' }
}
function Test-StagedSignatures([string] $Dir) {
    foreach ($bin in @('VisDir.App.exe', 'VisDir.Scanner.dll')) {
        $sig = Get-AuthenticodeSignature -LiteralPath (Join-Path $Dir $bin)
        if ($sig.Status -ne 'Valid') { throw ("Staged binary '{0}' failed Authenticode verification ({1})." -f $bin, $sig.Status) }
        $expected = ([string]$plan.ExpectedSignerThumbprint).Replace(' ', '').Replace("`t", '')
        if ($expected -ne '' -and $sig.SignerCertificate.Thumbprint -ne $expected) {
            throw ("Staged binary '{0}' was signed by an unexpected publisher." -f $bin)
        }
    }
}
function Invoke-UpdateSwap {
    Test-UpdatePlan
    Test-StagedSignatures ([System.IO.Path]::GetFullPath([string]$plan.StagedDir))
    $oldProcess = Get-Process -Id $plan.ProcessId -ErrorAction SilentlyContinue
    if ($oldProcess) { $oldProcess.WaitForExit(15000) }

    $appDir = [System.IO.Path]::GetFullPath([string]$plan.AppDir).TrimEnd('\', '/')
    $stagedDir = [System.IO.Path]::GetFullPath([string]$plan.StagedDir).TrimEnd('\', '/')
    $parent = Split-Path -Parent $appDir
    $leaf = Split-Path -Leaf $appDir
    $candidate = Join-Path $parent ($leaf + '.candidate-' + $plan.OperationId)
    $backup = Join-Path $parent ($leaf + '.previous')

    if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Recurse -Force }
    Copy-Item -Path (Join-Path $stagedDir '*') -Destination $candidate -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $candidate $plan.RelativeExe))) {
        throw 'Candidate installation is missing the application executable.'
    }
    Test-StagedSignatures $candidate

    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
    $movedOld = $false
    try {
        Move-Item -LiteralPath $appDir -Destination $backup
        $movedOld = $true
        Move-Item -LiteralPath $candidate -Destination $appDir
    } catch {
        if ($movedOld -and (Test-Path -LiteralPath $backup)) {
            if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }
            Move-Item -LiteralPath $backup -Destination $appDir
        }
        throw
    }

    Remove-Item -LiteralPath $plan.MarkerPath -Force -ErrorAction SilentlyContinue
    $env:VISDIR_UPDATE_HEALTH_MARKER = [string]$plan.MarkerPath
    $newProcess = Start-Process -FilePath (Join-Path $appDir $plan.RelativeExe) -PassThru
    $healthy = $false
    for ($i = 0; $i -lt 300; $i++) {
        if (Test-Path -LiteralPath $plan.MarkerPath) { $healthy = $true; break }
        if ($newProcess.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $healthy) {
        Write-UpdateLog 'New version failed startup health check; rolling back.'
        if (-not $newProcess.HasExited) { Stop-Process -Id $newProcess.Id -Force -ErrorAction SilentlyContinue }
        $failed = $appDir + '.failed-' + $plan.OperationId
        if (Test-Path -LiteralPath $failed) { Remove-Item -LiteralPath $failed -Recurse -Force }
        Move-Item -LiteralPath $appDir -Destination $failed
        Move-Item -LiteralPath $backup -Destination $appDir
        Start-InstalledApp $appDir
        throw 'The updated application did not report a healthy startup.'
    }
    Remove-Item -LiteralPath $plan.MarkerPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    Write-UpdateLog 'Update completed and passed startup health check.'
}

try {
    Invoke-UpdateSwap
} catch {
    if (-not $Elevated -and (Test-AccessDenied $_.Exception)) {
        $psHost = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments = @('-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
            '-File', ('"' + $PSCommandPath + '"'), '-PlanPath', ('"' + $PlanPath + '"'), '-Elevated')
        Start-Process -FilePath $psHost -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments | Out-Null
        exit 0
    }
    Start-InstalledApp ([string]$plan.AppDir)
    exit 1
}
""";
}
