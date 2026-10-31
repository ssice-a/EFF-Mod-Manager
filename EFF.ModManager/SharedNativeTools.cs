using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace EffModManager;

internal static class SharedNativeTools
{
    private const string ManifestFormat = "EFF_SHARED_NATIVE_TOOLS/1";
    private const string InstallFormat = "EFF_SHARED_NATIVE_TOOLS_INSTALL/1";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const string ReleaseApi = "https://api.github.com/repos/ssice-a/EFF/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    private sealed record InstallReceipt(string Format, string Sha256, string Release);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EFF-Shared-Tools/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static async Task<string> GetCompilerAsync(string resourceRoot, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(resourceRoot))
            throw new IOException("请先在设置中选择资源存储目录；共享编译器将安装到该目录的 tools 子文件夹。");

        var root = Path.GetFullPath(resourceRoot);
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        await using var gate = await AcquireLockAsync(Path.Combine(tools, "install.lock"), token);

        var current = ReadInstalled(tools);
        JsonDocument manifest;
        try { manifest = await ReadLatestManifestAsync(token); }
        catch (Exception error) when (current is not null && error is (HttpRequestException or IOException or TaskCanceledException))
        {
            return current;
        }

        using (manifest)
        {
            var data = ValidateManifest(manifest.RootElement);
            if (current is not null && string.Equals(ReadCurrentHash(tools), data.Sha256, StringComparison.OrdinalIgnoreCase))
                return current;
            return await DownloadAndInstallAsync(root, tools, data, token);
        }
    }

    internal static string? FindInstalledCompiler(string resourceRoot)
    {
        if (string.IsNullOrWhiteSpace(resourceRoot)) return null;
        return ReadInstalled(Path.Combine(Path.GetFullPath(resourceRoot), "tools"));
    }

    private static async Task<JsonDocument> ReadLatestManifestAsync(CancellationToken token)
    {
        using var response = await Client.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var release = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = release.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            throw new IOException("EFF 最新 Release 尚未发布共享工具清单。");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new IOException("EFF 最新 Release 缺少附件列表。");
        var manifests = assets.EnumerateArray().Where(item =>
            item.TryGetProperty("name", out var name) && name.GetString() == "eff-native-tools.json").ToArray();
        if (manifests.Length != 1 || !manifests[0].TryGetProperty("browser_download_url", out var urlElement))
            throw new IOException("EFF 最新 Release 未提供唯一的 eff-native-tools.json。");
        var url = RequireEffReleaseUrl(urlElement.GetString(), "eff-native-tools.json");
        using var manifestResponse = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        manifestResponse.EnsureSuccessStatusCode();
        var bytes = await manifestResponse.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("EFF 共享工具清单过大。");
        var manifest = JsonDocument.Parse(bytes);
        if (String(manifest.RootElement, "framework") != String(root, "tag_name"))
        {
            manifest.Dispose();
            throw new IOException("EFF 共享工具清单与最新 Release 标签不一致。");
        }
        return manifest;
    }

    private sealed record ManifestData(string Url, string Sha256, long Bytes, string Release);

    private static ManifestData ValidateManifest(JsonElement value)
    {
        if (String(value, "format") != ManifestFormat) throw new IOException("不支持的 EFF 共享工具清单格式。");
        var release = String(value, "framework");
        var url = RequireEffReleaseUrl(String(value, "url"), null);
        var uri = new Uri(url);
        var pathParts = uri.AbsolutePath.Trim('/').Split('/');
        if (pathParts.Length != 6 || !string.Equals(pathParts[4], release, StringComparison.Ordinal) ||
            !pathParts[5].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new IOException("共享工具包 URL 与清单中的 EFF Release 不一致。");
        var bytes = value.GetProperty("bytes").GetInt64();
        if (bytes <= 0 || bytes > MaxArchiveBytes) throw new IOException("EFF 共享工具包大小无效。");
        var hash = String(value, "sha256").ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new IOException("EFF 共享工具包 SHA-256 无效。");
        if (String(value, "archiveRoot") != "EFF_Native_Tools" ||
            String(value, "compiler") != "nativepack/eff_resource_pack.exe" ||
            String(value, "authorAssets") != "nativepack/eff_author_assets.dll" ||
            String(value, "indexUpdater") != "nativepack/eff_source_index.exe" ||
            String(value, "authorCore") != "nativepack/eff_author_core.dll" ||
            String(value, "protocol") != "nativepack/package-protocol.json")
            throw new IOException("EFF 共享工具清单中的文件布局不受支持。");
        return new ManifestData(url, hash, bytes, release);
    }

    private static string String(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(item.GetString()))
            throw new IOException("EFF 共享工具清单缺少 " + property + "。");
        return item.GetString()!;
    }

    private static string RequireEffReleaseUrl(string? value, string? expectedFile)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new IOException("共享工具下载地址必须来自 EFF GitHub Release。");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 6 || parts[0] != "ssice-a" || parts[1] != "EFF" ||
            parts[2] != "releases" || parts[3] != "download" ||
            expectedFile is not null && !string.Equals(parts[5], expectedFile, StringComparison.Ordinal))
            throw new IOException("EFF 共享工具下载地址无效。");
        return uri.AbsoluteUri;
    }

    private static string? ReadCurrentHash(string tools)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(tools, "current.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return receipt?.Format == InstallFormat ? receipt.Sha256 : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string? ReadInstalled(string tools)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(tools, "current.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (receipt?.Format != InstallFormat || receipt.Sha256.Length != 64 || receipt.Sha256.Any(c => !Uri.IsHexDigit(c))) return null;
            var directory = Path.GetFullPath(Path.Combine(tools, receipt.Sha256.ToLowerInvariant()));
            if (!IsWithin(tools, directory)) return null;
            var native = Path.Combine(directory, "nativepack");
            var required = new[] { "eff_resource_pack.exe", "eff_author_assets.dll", "eff_source_index.exe", "eff_author_core.dll", "package-protocol.json" };
            return required.All(name => File.Exists(Path.Combine(native, name))) && HasPillowAbis(directory)
                ? Path.Combine(native, "eff_resource_pack.exe") : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException) { return null; }
    }

    private static async Task<string> DownloadAndInstallAsync(string root, string tools, ManifestData manifest, CancellationToken token)
    {
        var staging = Path.Combine(tools, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "native-tools.zip");
        var payload = Path.Combine(staging, "payload");
        var destination = Path.Combine(tools, manifest.Sha256);
        try
        {
            using var response = await Client.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > manifest.Bytes) throw new IOException("EFF 共享工具包超过清单记录大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (total != manifest.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("EFF 共享工具包大小或 SHA-256 校验失败。");
            }

            Directory.CreateDirectory(payload);
            Extract(archivePath, payload, root);
            if (Directory.Exists(destination))
            {
                if (!HasRequiredFiles(destination))
                {
                    Directory.Delete(destination, true);
                    Directory.Move(payload, destination);
                }
            }
            else Directory.Move(payload, destination);
            var receipt = new InstallReceipt(InstallFormat, manifest.Sha256, manifest.Release);
            var currentTemp = Path.Combine(tools, "current." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(currentTemp, JsonSerializer.Serialize(receipt,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }), token);
                File.Move(currentTemp, Path.Combine(tools, "current.json"), true);
            }
            finally { if (File.Exists(currentTemp)) File.Delete(currentTemp); }
            return Path.Combine(destination, "nativepack", "eff_resource_pack.exe");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void Extract(string archivePath, string destination, string resourceRoot)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nativepack/eff_resource_pack.exe", "nativepack/eff_author_assets.dll", "nativepack/eff_source_index.exe",
            "nativepack/eff_author_core.dll", "nativepack/package-protocol.json",
            "vendor/PIL/__init__.py", "vendor/PIL/_imaging.cp311-win_amd64.pyd",
            "vendor/PIL/_imaging.cp313-win_amd64.pyd"
        };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var raw = entry.FullName;
            if (raw.Length == 0 || raw.Contains('\\') || raw.Contains(':') || raw.StartsWith('/') ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || !names.Add(raw))
                throw new IOException("EFF 共享工具包包含非法或重复路径。");
            var parts = raw.TrimEnd('/').Split('/');
            if (parts.Length == 0 || parts[0] != "EFF_Native_Tools" || parts.Any(part => part is "" or "." or ".."))
                throw new IOException("EFF 共享工具包包含越界路径。");
            if (parts.Length < 2 || parts[1] is not ("nativepack" or "vendor")) continue;
            if (raw.EndsWith('/')) continue;
            var relative = string.Join('/', parts.Skip(1));
            var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(parts.Skip(1).ToArray())));
            if (!IsWithin(destination, target) || !IsWithin(resourceRoot, target))
                throw new IOException("EFF 共享工具包目标路径越界。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
            found.Add(relative);
        }
        var missing = required.Except(found).ToArray();
        if (missing.Length > 0) throw new IOException("EFF 共享工具包缺少文件：" + string.Join(", ", missing));
    }

    private static bool HasRequiredFiles(string directory)
    {
        var native = Path.Combine(directory, "nativepack");
        return new[] { "eff_resource_pack.exe", "eff_author_assets.dll", "eff_source_index.exe", "eff_author_core.dll", "package-protocol.json" }
            .All(name => File.Exists(Path.Combine(native, name))) &&
            HasPillowAbis(directory);
    }

    private static bool HasPillowAbis(string directory) => new[]
    {
        "__init__.py", "_imaging.cp311-win_amd64.pyd", "_imaging.cp313-win_amd64.pyd"
    }.All(name => File.Exists(Path.Combine(directory, "vendor", "PIL", name)));

    private static bool IsWithin(string parent, string path)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 900) { await Task.Delay(100, token); }
        }
    }
}
