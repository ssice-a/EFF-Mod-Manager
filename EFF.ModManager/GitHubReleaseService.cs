using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EffModManager;

internal static partial class GitHubReleaseService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string EffRepository = "ssice-a/EFF";
    private const string ManagerRepository = "ssice-a/EFF-Mod-Manager";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EFF-Mod-Manager/0.1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static Task<GitHubRelease?> GetLatestEffAsync(CancellationToken token) => GetLatestAsync(EffRepository, token);
    internal static Task<GitHubRelease?> GetLatestManagerAsync(CancellationToken token) => GetLatestAsync(ManagerRepository, token);

    private static async Task<GitHubRelease?> GetLatestAsync(string repository, CancellationToken token)
    {
        using var response = await Client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, token);
    }

    internal static async Task<GitHubAsset?> GetLatestDllAssetAsync(CancellationToken token)
    {
        var release = await GetLatestEffAsync(token);
        return release?.assets
            .Where(asset => DllAssetVersion().Match(asset.name).Success)
            .OrderByDescending(asset => ParseAssetVersion(DllAssetVersion(), asset.name))
            .FirstOrDefault();
    }

    internal static async Task<(GitHubRelease? Release, GitHubAsset? Asset)> GetLatestRepairAssetAsync(CancellationToken token)
    {
        using var response = await Client.GetAsync($"https://api.github.com/repos/{EffRepository}/releases?per_page=10", token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, token) ?? [];
        var match = releases
            .SelectMany(release => release.assets.Where(asset => RepairAssetVersion().Match(asset.name).Success)
                .Select(asset => (Release: release, Asset: asset, Version: ParseAssetVersion(RepairAssetVersion(), asset.name))))
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();
        return match.Asset is null ? (null, null) : (match.Release, match.Asset);
    }

    internal static async Task<string> DownloadVerifiedAsync(GitHubAsset asset, string destination, IProgress<string>? progress, CancellationToken token)
    {
        if (asset.size < 0 || asset.size > 600L * 1024 * 1024) throw new InvalidDataException("Release asset size is outside the allowed limit.");
        if (!Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com"))
            throw new InvalidDataException("Release asset URL is not a trusted GitHub HTTPS URL.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != asset.size)
                throw new InvalidDataException("Downloaded release asset size does not match GitHub metadata.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072];
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, token);
                if (read == 0) break;
                copied += read;
                if (copied > asset.size || copied > 600L * 1024 * 1024) throw new InvalidDataException("Downloaded release asset exceeds the advertised size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                if (asset.size > 0) progress?.Report($"{asset.name}: {copied * 100 / asset.size}%");
            }
            if (copied != asset.size) throw new InvalidDataException("Downloaded release asset is incomplete.");
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(asset.digest))
            {
                var expected = asset.digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? asset.digest[7..] : asset.digest;
                if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release asset SHA-256 verification failed.");
            }
            await output.DisposeAsync();
            File.Move(temporary, destination, true);
            return digest;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static Version DllVersion(string assetName) => ParseAssetVersion(DllAssetVersion(), assetName);
    internal static Version RepairVersion(string assetName) => ParseAssetVersion(RepairAssetVersion(), assetName);

    private static Version ParseAssetVersion(Regex regex, string name)
    {
        var match = regex.Match(name);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version) ? version : new Version(0, 0);
    }

    [GeneratedRegex(@"^EFF_v(?<version>\d+(?:\.\d+){1,3})_dll\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex DllAssetVersion();
    [GeneratedRegex(@"^EFF-Repair-v(?<version>\d+(?:\.\d+){1,3})-win-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex RepairAssetVersion();
}
