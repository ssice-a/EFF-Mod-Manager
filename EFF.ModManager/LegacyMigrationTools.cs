using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace EffModManager;

internal static class LegacyMigrationTools
{
    private const string ManifestFormat = "EFF_SHARED_NATIVE_TOOLS/3";
    private const string InstallFormat = "EFF_SHARED_NATIVE_TOOLS_INSTALL/3";
    private const string PrivateInstallFormat = "EFF_LEGACY_MIGRATOR_INSTALL/1";
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const string ReleaseApi = "https://api.github.com/repos/ssice-a/EFF/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    private sealed record InstallReceipt(string Format, string Sha256, string Release);
    private sealed record ManifestData(string Url, string Sha256, long Bytes, string Release, string ArchiveName);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EFF-Legacy-Migration/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static string? FindInstalledMigrator(string resourceRoot)
    {
        if (string.IsNullOrWhiteSpace(resourceRoot)) return null;
        try
        {
            var tools = Path.Combine(Path.GetFullPath(resourceRoot), "tools");
            return ReadInstalled(tools);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    internal static async Task<string> GetMigratorAsync(string resourceRoot, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(resourceRoot))
            throw new IOException("Choose a shared resource folder in Settings before preparing migration tools.");

        var root = Path.GetFullPath(resourceRoot);
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        await using var gate = await AcquireLockAsync(Path.Combine(tools, "install.lock"), token);

        var current = ReadInstalled(tools);
        JsonDocument manifest;
        try { manifest = await ReadLatestManifestAsync(token); }
        catch (Exception error) when (current is not null && error is (HttpRequestException or IOException or TaskCanceledException))
        { return current; }

        using (manifest)
        {
            var data = ValidateManifest(manifest.RootElement);
            if (current is not null && ReadCurrentHash(tools, current) == data.Sha256)
                return current;
            return await DownloadAndInstallAsync(root, tools, data, token);
        }
    }

    private static string? ReadInstalled(string tools)
    {
        var shared = ReadInstalledReceipt(tools, Path.Combine(tools, "current.json"), InstallFormat);
        if (shared is not null) return shared;
        return ReadInstalledReceipt(tools, Path.Combine(tools, "legacy-migration.json"), PrivateInstallFormat);
    }

    private static string? ReadInstalledReceipt(string tools, string receiptPath, string expectedFormat)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(receiptPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (receipt?.Format != expectedFormat || !ValidHash(receipt.Sha256)) return null;
            var directory = Path.GetFullPath(Path.Combine(tools, receipt.Sha256.ToLowerInvariant()));
            if (!IsWithin(tools, directory) || !HasRequiredFiles(directory)) return null;
            return Path.Combine(directory, "nativepack", "eff_migrate_mods.exe");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        { return null; }
    }

    private static string? ReadCurrentHash(string tools, string executable)
    {
        var directory = Directory.GetParent(Directory.GetParent(executable)!.FullName)!.FullName;
        return Path.GetFileName(directory);
    }

    private static async Task<JsonDocument> ReadLatestManifestAsync(CancellationToken token)
    {
        using var response = await Client.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var release = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = release.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            throw new IOException("The latest EFF release is still a draft and has no published migration tools.");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new IOException("The latest EFF release does not contain an asset list.");
        var manifests = assets.EnumerateArray().Where(item =>
            item.TryGetProperty("name", out var name) && name.GetString() == "eff-native-tools.json").ToArray();
        if (manifests.Length != 1 || !manifests[0].TryGetProperty("browser_download_url", out var urlElement))
            throw new IOException("The latest EFF release does not provide a unique shared-tools manifest.");
        var url = RequireEffReleaseUrl(urlElement.GetString(), "eff-native-tools.json");
        using var manifestResponse = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        manifestResponse.EnsureSuccessStatusCode();
        var bytes = await manifestResponse.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("The shared-tools manifest is too large.");
        var manifest = JsonDocument.Parse(bytes);
        if (String(manifest.RootElement, "framework") != String(root, "tag_name"))
        {
            manifest.Dispose();
            throw new IOException("The shared-tools manifest does not match the latest EFF release.");
        }
        return manifest;
    }

    private static ManifestData ValidateManifest(JsonElement value)
    {
        if (String(value, "format") != ManifestFormat) throw new IOException("Unsupported shared-tools manifest format.");
        var release = String(value, "framework");
        var url = RequireEffReleaseUrl(String(value, "url"), null);
        var uri = new Uri(url);
        var pathParts = uri.AbsolutePath.Trim('/').Split('/');
        var archiveName = "EFF_Shared_Native_Tools_" + release + ".zip";
        if (pathParts.Length != 6 || !string.Equals(pathParts[4], release, StringComparison.Ordinal) ||
            !string.Equals(pathParts[5], archiveName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The shared-tools URL does not identify this EFF release archive.");
        var bytes = value.GetProperty("bytes").GetInt64();
        if (bytes <= 0 || bytes > MaxArchiveBytes) throw new IOException("The shared-tools archive size is invalid.");
        var hash = String(value, "sha256").ToLowerInvariant();
        if (!ValidHash(hash)) throw new IOException("The shared-tools archive SHA-256 is invalid.");
        var expected = new Dictionary<string, string>
        {
            ["archiveRoot"] = "EFF_Native_Tools",
            ["compiler"] = "nativepack/eff_resource_pack.exe",
            ["indexUpdater"] = "nativepack/eff_source_index.exe",
            ["protocol"] = "nativepack/package-protocol.json",
            ["authorAssets"] = "nativepack/eff_author_assets.dll",
            ["authorCore"] = "nativepack/eff_author_core.dll",
            ["textureCompressor"] = "nativepack/texconv.exe",
            ["legacyMigrator"] = "nativepack/eff_migrate_mods.exe",
            ["migrationSourceExporter"] = "nativepack/source-exporter/eff_migrate_sources.exe"
        };
        if (expected.Any(pair => String(value, pair.Key) != pair.Value))
            throw new IOException("The shared-tools manifest has an unsupported file layout.");
        return new ManifestData(url, hash, bytes, release, archiveName);
    }

    private static string String(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(item.GetString()))
            throw new IOException("The shared-tools manifest is missing " + property + ".");
        return item.GetString()!;
    }

    private static string RequireEffReleaseUrl(string? value, string? expectedFile)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Shared-tool downloads must come from EFF GitHub Releases.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 6 || parts[0] != "ssice-a" || parts[1] != "EFF" ||
            parts[2] != "releases" || parts[3] != "download" ||
            expectedFile is not null && !string.Equals(parts[5], expectedFile, StringComparison.Ordinal))
            throw new IOException("The EFF shared-tool download URL is invalid.");
        return uri.AbsoluteUri;
    }

    private static async Task<string> DownloadAndInstallAsync(string root, string tools, ManifestData manifest, CancellationToken token)
    {
        var staging = Path.Combine(tools, ".migration-install-" + Guid.NewGuid().ToString("N"));
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
                    if (total > manifest.Bytes) throw new IOException("The shared-tools archive exceeds its published size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (total != manifest.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The shared-tools archive size or SHA-256 check failed.");
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

            var currentPath = CurrentReceiptPath(tools);
            var receipt = new InstallReceipt(currentPath.EndsWith("legacy-migration.json", StringComparison.OrdinalIgnoreCase)
                ? PrivateInstallFormat : InstallFormat, manifest.Sha256, manifest.Release);
            var currentTemp = Path.Combine(Path.GetDirectoryName(currentPath)!, "migration." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(currentTemp, JsonSerializer.Serialize(receipt,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }), token);
                File.Move(currentTemp, currentPath, true);
            }
            finally { if (File.Exists(currentTemp)) File.Delete(currentTemp); }
            return Path.Combine(destination, "nativepack", "eff_migrate_mods.exe");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static string CurrentReceiptPath(string tools)
    {
        var sharedReceipt = Path.Combine(tools, "current.json");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(sharedReceipt));
            var format = String(json.RootElement, "format");
            if (format.StartsWith("EFF_SHARED_NATIVE_TOOLS_INSTALL/", StringComparison.Ordinal) &&
                format != InstallFormat)
                return Path.Combine(tools, "legacy-migration.json");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return sharedReceipt;
    }

    internal static void Extract(string archivePath, string destination, string resourceRoot)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nativepack/eff_author_assets.dll", "nativepack/eff_author_core.dll",
            "nativepack/texconv.exe", "nativepack/eff_migrate_mods.exe",
            "nativepack/source-exporter/eff_migrate_sources.exe",
            "nativepack/source-exporter/AnimeStudio.GUI.dll",
            "nativepack/source-exporter/AnimeStudio.GUI.deps.json",
            "nativepack/source-exporter/AnimeStudio.GUI.runtimeconfig.json",
            "nativepack/THIRD_PARTY_NOTICES", "vendor/PIL/__init__.py",
            "vendor/PIL/_imaging.cp311-win_amd64.pyd", "vendor/PIL/_imaging.cp313-win_amd64.pyd"
        };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 50000) throw new IOException("The shared-tools archive contains too many entries.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var raw = entry.FullName;
            if (raw.Length == 0 || raw.Contains('\\') || raw.Contains(':') || raw.StartsWith('/') ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || !names.Add(raw))
                throw new IOException("The shared-tools archive contains an unsafe or duplicate path.");
            var parts = raw.TrimEnd('/').Split('/');
            if (parts.Length < 2 || parts[0] != "EFF_Native_Tools" || parts.Any(part => part is "" or "." or "..") ||
                parts[1] is not ("nativepack" or "vendor"))
                throw new IOException("The shared-tools archive contains a path outside its root.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new IOException("The shared-tools archive expands beyond the safety limit.");
            if (raw.EndsWith('/')) continue;
            var relative = string.Join('/', parts.Skip(1));
            var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(parts.Skip(1).ToArray())));
            if (!IsWithin(destination, target) || !IsWithin(resourceRoot, target))
                throw new IOException("A shared-tools archive path escapes the selected resource folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
            found.Add(relative);
        }
        var missing = required.Except(found).ToArray();
        if (missing.Length > 0) throw new IOException("The shared-tools archive is missing: " + string.Join(", ", missing));
    }

    private static bool HasRequiredFiles(string directory)
    {
        var native = Path.Combine(directory, "nativepack");
        var vendor = Path.Combine(directory, "vendor", "PIL");
        var required = new[]
        {
            "eff_author_assets.dll", "eff_author_core.dll", "texconv.exe",
            "eff_migrate_mods.exe", "THIRD_PARTY_NOTICES"
        };
        return required.All(name => File.Exists(Path.Combine(native, name))) &&
            new[] { "eff_migrate_sources.exe", "AnimeStudio.GUI.dll", "AnimeStudio.GUI.deps.json",
                "AnimeStudio.GUI.runtimeconfig.json" }.All(name =>
                    File.Exists(Path.Combine(native, "source-exporter", name))) &&
            File.Exists(Path.Combine(vendor, "__init__.py")) &&
            File.Exists(Path.Combine(vendor, "_imaging.cp311-win_amd64.pyd")) &&
            File.Exists(Path.Combine(vendor, "_imaging.cp313-win_amd64.pyd"));
    }

    private static bool IsWithin(string parent, string path)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

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
