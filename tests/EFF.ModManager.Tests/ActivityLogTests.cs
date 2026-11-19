using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using EffModManager;

internal static class ActivityLogTests
{
    internal static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "logs");
        var log = new ActivityLog();
        log.Configure(folder);
        var indexFolder = Path.Combine(root, "resources");
        var direct = "https://github.com/ssice-a/EFF/releases/download/v1.0/fixture.effindex.gz";
        var timeout = ActivityLog.WithRecovery(new TimeoutException("fixture timed out"), RecoveryHelp.Index(indexFolder, "", direct));
        log.Failure("log.index", timeout);
        var entry = log.Snapshot().Single();
        Require(entry.Reason == "log.timeout" && entry.Recovery!.Links.Contains(direct) &&
            entry.Recovery.Links.Contains(RecoveryHelp.EffIndexCatalog) && entry.Steps.Contains(indexFolder),
            "Timeout lost its actual URL, index catalog link or local recovery directory.");
        Require(ActivityLog.Classify(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)) == "log.rateLimit" &&
            ActivityLog.Classify(new FileNotFoundException("missing index")) == "log.missing", "Failure causes were conflated.");

        var english = LanguageService.Instance.IsEnglish;
        try
        {
            LanguageService.Instance.IsEnglish = false;
            var chinese = entry.Steps;
            LanguageService.Instance.IsEnglish = true;
            Require(entry.Steps != chinese && entry.Steps.Contains("Import local index"), "Existing log recovery did not follow the selected language.");
        }
        finally { LanguageService.Instance.IsEnglish = english; }

        // Preserve history and tolerate a partial last line after an interrupted process.
        File.AppendAllText(log.FilePath, "{truncated\n");
        var recovered = new ActivityLog();
        recovered.Configure(folder);
        Require(recovered.Snapshot().Single().Recovery!.Links.Contains(direct), "Persisted failure context was not recovered.");
        Parallel.For(0, 450, i => recovered.Info("log.action", "event " + i));
        Require(recovered.Snapshot().Length == 400, "In-memory log is unbounded.");
        File.AppendAllText(recovered.FilePath, new string(' ', 2 * 1024 * 1024));
        recovered.Info("log.action", "after rotation");
        Require(File.Exists(recovered.FilePath + ".previous") && new FileInfo(recovered.FilePath).Length < 4096, "Persistent log did not rotate.");

        var blocked = Path.Combine(root, "not-a-folder");
        File.WriteAllText(blocked, "fixture");
        var unavailable = new ActivityLog();
        unavailable.Configure(blocked);
        unavailable.Failure("log.action", new IOException("still visible"));
        Require(unavailable.Snapshot().Length == 1 && unavailable.PersistenceError.Length > 0,
            "A read-only/unavailable log destination hid the original failure.");
        Console.WriteLine("PASS: logs preserve causes, actual URLs and recovery paths across restarts/languages; concurrent writes, rotation and disk failures remain bounded.");

        await VerifyManualToolsAsync(root);
    }

    private static async Task VerifyManualToolsAsync(string root)
    {
        // Install uses the same non-system resource policy as production.
        var resources = Path.Combine(root, "offline-tools");
        if (SettingsStore.IsSystemDrivePath(resources))
            resources = Path.Combine(AppContext.BaseDirectory, "offline-tools-" + Guid.NewGuid().ToString("N"));
        if (SettingsStore.IsSystemDrivePath(resources)) throw new IOException("Run these installer tests with TEMP on a non-system drive.");
        var downloads = Path.Combine(resources, "tools", "downloads");
        Directory.CreateDirectory(downloads);
        try
        {
            var zipName = "EFF_Shared_Native_Tools_v1.0.0.zip";
            var zipPath = Path.Combine(downloads, zipName);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var name in new[] {
                    "nativepack/eff_resource_pack.exe", "nativepack/eff_source_index.exe", "nativepack/package-protocol.json",
                    "nativepack/eff_author_assets.dll", "nativepack/eff_author_core.dll", "nativepack/texconv.exe", "nativepack/eff_migrate_mods.exe",
                    "nativepack/THIRD_PARTY_NOTICES", "nativepack/source-exporter/eff_migrate_sources.exe", "nativepack/source-exporter/AnimeStudio.GUI.dll",
                    "nativepack/source-exporter/AnimeStudio.GUI.deps.json", "nativepack/source-exporter/AnimeStudio.GUI.runtimeconfig.json",
                    "vendor/PIL/__init__.py", "vendor/PIL/_imaging.cp311-win_amd64.pyd", "vendor/PIL/_imaging.cp313-win_amd64.pyd" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry("EFF_Native_Tools/" + name).Open());
                    writer.Write("fixture");
                }
            }
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
            var manifest = new Dictionary<string, object>
            {
                ["format"] = "EFF_SHARED_NATIVE_TOOLS/3", ["framework"] = "v1.0.0",
                ["url"] = "https://github.com/ssice-a/EFF/releases/download/v1.0.0/" + zipName,
                ["sha256"] = hash, ["bytes"] = new FileInfo(zipPath).Length, ["archiveRoot"] = "EFF_Native_Tools",
                ["compiler"] = "nativepack/eff_resource_pack.exe", ["indexUpdater"] = "nativepack/eff_source_index.exe",
                ["protocol"] = "nativepack/package-protocol.json", ["authorAssets"] = "nativepack/eff_author_assets.dll",
                ["authorCore"] = "nativepack/eff_author_core.dll", ["textureCompressor"] = "nativepack/texconv.exe",
                ["legacyMigrator"] = "nativepack/eff_migrate_mods.exe", ["migrationSourceExporter"] = "nativepack/source-exporter/eff_migrate_sources.exe"
            };
            File.WriteAllText(Path.Combine(downloads, "eff-native-tools.json"), JsonSerializer.Serialize(manifest));
            // Corrupt local bytes must fail before any request, receipt or executable publication.
            File.AppendAllText(zipPath, "broken");
            try { await LegacyMigrationTools.GetMigratorAsync(resources, CancellationToken.None); throw new InvalidOperationException("Corrupt manual tools were accepted."); }
            catch (IOException error)
            {
                Require(error.Data[nameof(RecoveryAdvice)] is RecoveryAdvice advice && advice.Folder == downloads,
                    "Rejected offline package had no actionable recovery destination.");
            }
            using (var file = File.OpenWrite(zipPath)) file.SetLength((long)manifest["bytes"]);
            var installed = await LegacyMigrationTools.GetMigratorAsync(resources, CancellationToken.None);
            Require(File.Exists(installed) && LegacyMigrationTools.FindInstalledMigrator(resources) == installed,
                "Manually downloaded tools did not use the production validated installer/receipt.");
            Console.WriteLine("PASS: manual tools install without a network lookup; bad bytes are rejected before publication, valid archives create the normal installation receipt.");
        }
        finally { Directory.Delete(resources, true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
