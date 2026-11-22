using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace FolderCleaner.Services;

public sealed record UpdateInfo(
    Version LatestVersion,
    string TagName,
    string DownloadUrl,
    string ReleasePageUrl,
    string ReleaseNotes);

public sealed class UpdateService
{
    public const string RepoOwner = "worldruschel";
    public const string RepoName = "folder-cleaner";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? new Version(1, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentVersionDisplay => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(LatestReleaseApiUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
        if (!TryParseVersion(tagName, out var latestVersion))
        {
            return null;
        }

        if (latestVersion <= CurrentVersion)
        {
            return null;
        }

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url)) continue;

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = url;
                    if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            return null;
        }

        string htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? "" : "";
        string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

        return new UpdateInfo(latestVersion, tagName, downloadUrl, htmlUrl, body.Trim());
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        string tempFile = Path.Combine(
            Path.GetTempPath(),
            $"FolderCleaner_Setup_{update.LatestVersion.Major}_{update.LatestVersion.Minor}_{update.LatestVersion.Build}.exe");

        using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        byte[] buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (totalBytes is > 0)
            {
                progress?.Report((double)totalRead / totalBytes.Value * 100.0);
            }
        }

        progress?.Report(100.0);
        return tempFile;
    }

    public static void LaunchInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /AUTOUPDATE=1",
            UseShellExecute = true
        });
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        string cleaned = tag.Trim().TrimStart('v', 'V');
        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0) cleaned = cleaned[..dashIdx];

        if (!Version.TryParse(cleaned, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FolderCleaner/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
