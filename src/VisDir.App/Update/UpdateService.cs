using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
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
    string HtmlUrl
);

public sealed class UpdateService
{
    private const string RepoOwner = "dklasens";
    private const string RepoName = "VisDir";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VisDir-App", GetCurrentVersion().ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is not null ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
    }

    public async Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubReleaseDto>(json);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return null;

            string versionStr = release.TagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionStr, out Version? releaseVersion))
            {
                if (Version.TryParse(versionStr + ".0", out Version? v2))
                    releaseVersion = v2;
                else
                    return null;
            }

            Version currentVersion = GetCurrentVersion();
            if (releaseVersion <= currentVersion) return null;

            string targetAsset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "VisDir-win-arm64.zip"
                : "VisDir-win-x64.zip";

            var asset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, targetAsset, StringComparison.OrdinalIgnoreCase));

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return null;

            return new ReleaseInfo(
                TagName: release.TagName,
                Version: releaseVersion,
                ReleaseNotes: release.Body ?? "No release notes provided.",
                DownloadUrl: asset.BrowserDownloadUrl,
                AssetSizeBytes: asset.Size,
                HtmlUrl: release.HtmlUrl ?? $"https://github.com/{RepoOwner}/{RepoName}/releases"
            );
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> DownloadUpdateAsync(
        ReleaseInfo release,
        IProgress<(long BytesDownloaded, long TotalBytes, double Fraction)>? progress = null,
        CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VisDir", "Updates");
        Directory.CreateDirectory(tempDir);
        string zipPath = Path.Combine(tempDir, $"VisDir-{release.TagName}.zip");

        if (File.Exists(zipPath))
            File.Delete(zipPath);

        using var response = await HttpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? release.AssetSizeBytes;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;
            double fraction = totalBytes > 0 ? (double)totalRead / totalBytes : 0;
            progress?.Report((totalRead, totalBytes, Math.Min(1.0, fraction)));
        }

        return zipPath;
    }

    public static void ApplyUpdateAndRestart(string zipPath)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "VisDir", "Updates");
        string stagedDir = Path.Combine(tempRoot, "Staged");

        if (Directory.Exists(stagedDir))
            Directory.Delete(stagedDir, true);

        Directory.CreateDirectory(stagedDir);

        // Safe extraction
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string destPath = Path.GetFullPath(Path.Combine(stagedDir, entry.FullName));
                if (!destPath.StartsWith(stagedDir, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Zip slip detected in update archive");

                string? dir = Path.GetDirectoryName(destPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                entry.ExtractToFile(destPath, true);
            }
        }

        string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string currentExe = Environment.ProcessPath ?? Path.Combine(appDir, "VisDir.App.exe");
        int currentPid = Environment.ProcessId;

        // Generate robust PowerShell update launcher script
        string scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
        string script = $@"
Start-Sleep -Milliseconds 600
$proc = Get-Process -Id {currentPid} -ErrorAction SilentlyContinue
if ($proc) {{ $proc.WaitForExit(10000) }}

try {{
    Copy-Item -Path '{stagedDir}\*' -Destination '{appDir}' -Recurse -Force -ErrorAction Stop
    Start-Process -FilePath '{currentExe}'
}} catch {{
    Start-Process powershell -Verb RunAs -ArgumentList ""-WindowStyle Hidden -ExecutionPolicy Bypass -Command `""Copy-Item -Path '{stagedDir}\*' -Destination '{appDir}' -Recurse -Force; Start-Process -FilePath '{currentExe}'`""""
}}
";

        File.WriteAllText(scriptPath, script);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
        };

        Process.Start(startInfo);
        Application.Current.Shutdown();
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
