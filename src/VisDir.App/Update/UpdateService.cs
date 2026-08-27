using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

public sealed class UpdateService
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

        return new ReleaseInfo(
            release.TagName,
            releaseVersion,
            release.Body ?? "No release notes provided.",
            asset.BrowserDownloadUrl,
            asset.Size,
            NormalizeDigest(asset.Digest),
            release.HtmlUrl ?? $"https://github.com/{RepoOwner}/{RepoName}/releases");
    }

    public async Task<string> DownloadUpdateAsync(
        ReleaseInfo release,
        IProgress<(long BytesDownloaded, long TotalBytes, double Fraction)>? progress = null,
        CancellationToken ct = default)
    {
        string tempDir = GetUpdateTempRoot();
        Directory.CreateDirectory(tempDir);
        string zipPath = Path.Combine(tempDir, $"VisDir-{release.Version}.zip");
        string partialPath = zipPath + ".partial";
        TryDeleteFile(partialPath);

        try
        {
            using var response = await HttpClient.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? release.AssetSizeBytes;
            if (totalBytes is <= 0 or > MaxDownloadBytes)
                throw new InvalidDataException($"Update download size {totalBytes:N0} is outside the accepted range.");

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
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

            File.Move(partialPath, zipPath, overwrite: true);
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
        string appDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        string currentExe = Path.GetFullPath(Environment.ProcessPath ?? Path.Combine(appDir, "VisDir.App.exe"));
        string relativeExe = Path.GetRelativePath(appDir, currentExe);
        if (relativeExe.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeExe))
            throw new InvalidOperationException("The running executable is outside the application directory.");

        string tempRoot = GetUpdateTempRoot();
        Directory.CreateDirectory(tempRoot);
        string operationId = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(tempRoot, $"apply-update-{operationId}.ps1");
        string planPath = Path.Combine(tempRoot, $"apply-update-{operationId}.json");
        string markerPath = Path.Combine(tempRoot, $"healthy-{operationId}.marker");
        var plan = new UpdatePlan(appDir, stagedDir, relativeExe, Environment.ProcessId, operationId, markerPath);
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        File.WriteAllText(scriptPath, UpdateScript);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
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

    public static void ReportHealthyStart()
    {
        string? marker = Environment.GetEnvironmentVariable(HealthMarkerEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(marker)) return;
        try
        {
            string fullPath = Path.GetFullPath(marker);
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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record UpdatePlan(
        string AppDir, string StagedDir, string RelativeExe, int ProcessId, string OperationId, string MarkerPath);

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
function Test-AccessDenied([Exception] $Exception) {
    for ($current = $Exception; $null -ne $current; $current = $current.InnerException) {
        if ($current -is [System.UnauthorizedAccessException] -or $current.HResult -eq -2147024891) {
            return $true
        }
    }
    return $false
}
function Invoke-UpdateSwap {
    $oldProcess = Get-Process -Id $plan.ProcessId -ErrorAction SilentlyContinue
    if ($oldProcess) { $oldProcess.WaitForExit(15000) }

    $appDir = [System.IO.Path]::GetFullPath([string]$plan.AppDir).TrimEnd('\', '/')
    $stagedDir = [System.IO.Path]::GetFullPath([string]$plan.StagedDir).TrimEnd('\', '/')
    $parent = Split-Path -Parent $appDir
    $leaf = Split-Path -Leaf $appDir
    $candidate = Join-Path $parent ($leaf + '.candidate-' + $plan.OperationId)
    $backup = Join-Path $parent ($leaf + '.previous')

    if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Recurse -Force }
    New-Item -ItemType Directory -Path $candidate -Force | Out-Null
    Copy-Item -Path (Join-Path $stagedDir '*') -Destination $candidate -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $candidate $plan.RelativeExe))) {
        throw 'Candidate installation is missing the application executable.'
    }

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
    Write-UpdateLog $_.Exception.ToString()
    if (-not $Elevated -and (Test-AccessDenied $_.Exception)) {
        $arguments = @('-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
            '-File', ('"' + $PSCommandPath + '"'), '-PlanPath', ('"' + $PlanPath + '"'), '-Elevated')
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments | Out-Null
        exit 0
    }
    Start-InstalledApp ([string]$plan.AppDir)
    exit 1
}
""";
}
