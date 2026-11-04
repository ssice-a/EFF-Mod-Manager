using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AnimeStudio.GUI;
using EffRepair;

namespace EffModManager;

internal sealed record LegacyModSource(string FolderName, string Id, string Name);
internal sealed record LegacyMigrationSummary(int Migrated, int MigratedEffects, IReadOnlyList<string> Names);

internal static class LegacyMigrationService
{
    internal static string BackupRoot(string game) => Path.Combine(game, "plugin", "backup");

    internal static IReadOnlyList<LegacyModSource> InspectBackup(string backupRoot)
    {
        if (!Directory.Exists(backupRoot)) return [];
        var directories = Directory.EnumerateDirectories(backupRoot)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        if (directories.Length == 0) return [];

        var result = new List<LegacyModSource>(directories.Length);
        foreach (var directory in directories)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links are not supported in the backup folder: " + directory);
            var mods = ModService.ScanTree(directory);
            if (mods.Count != 1 || !string.Equals(Path.GetFullPath(mods[0].Folder), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Each immediate backup folder must contain exactly one Mod at its root: " + Path.GetFileName(directory));
            var mod = mods[0];
            if (mod.Format != "2")
                throw new InvalidDataException("Only Format 2 Mods can be migrated: " + Path.GetFileName(directory));
            if (string.IsNullOrWhiteSpace(mod.Id))
                throw new InvalidDataException("The backup Mod has no ID: " + Path.GetFileName(directory));
            result.Add(new LegacyModSource(Path.GetFileName(directory), mod.Id, mod.Name));
        }

        var duplicate = result.GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException("The backup contains duplicate Mod IDs: " + duplicate.Key);
        return result;
    }

    internal static async Task<string> ResolveWorkspaceAsync(string game, string selected, string resourceRoot,
        IProgress<string>? progress, CancellationToken token, HttpClient? indexClient = null)
    {
        var archive = await Task.Run(() => EndfieldVfsArchive.Open(game), token);
        var gamePath = EndfieldSharedIndexes.Game(archive);
        var defaultWorkspace = EndfieldSharedIndexes.DefaultWorkspace(gamePath, resourceRoot);
        var currentWorkspace = EndfieldSharedIndexes.FindCurrent(archive, token, selected, defaultWorkspace);
        if (!string.IsNullOrWhiteSpace(currentWorkspace)) return currentWorkspace;

        if (string.IsNullOrWhiteSpace(resourceRoot))
            throw new IOException("Choose a shared resource folder in Settings to download and store the public game index.");

        var content = EndfieldPortableIndex.ContentIdentity(archive);
        progress?.Report("Finding a public index that matches this game's Bundle contents...");
        var published = await EndfieldIndexUpdates.Find(EndfieldIndexUpdates.DefaultSource, content, token, indexClient!);
        if (published is null || !string.Equals(published.Release.GameContent, content, StringComparison.OrdinalIgnoreCase))
            throw new IOException("No public index matches this game's exact Bundle contents. No Mods were migrated.");

        Directory.CreateDirectory(defaultWorkspace);
        var package = EndfieldIndexUpdates.CachedPackage(published, resourceRoot);
        progress?.Report("Downloading and verifying the matching public game index...");
        await EndfieldIndexUpdates.Download(published, package, progress ?? new Progress<string>(_ => { }), token, indexClient!);
        token.ThrowIfCancellationRequested();
        archive.VerifyCurrent();
        progress?.Report("Importing the public index and validating it against local game Bundles...");
        var indexPath = EndfieldPortableIndex.Import(archive, defaultWorkspace, package, content, token);
        if (!EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, defaultWorkspace, out var currentIndex, token) ||
            !File.Exists(currentIndex + ".bundles") ||
            !string.Equals(Path.GetFullPath(indexPath), Path.GetFullPath(currentIndex), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded public index did not produce a complete local index workspace.");
        EndfieldSharedIndexes.TryRemember(archive, defaultWorkspace, token);
        return defaultWorkspace;
    }

    internal static async Task<LegacyMigrationSummary> MigrateAsync(string game, string resourceRoot, string workspace,
        IProgress<string>? progress, CancellationToken token)
    {
        game = Path.GetFullPath(game);
        if (!GameDiscovery.IsGame(game)) throw new IOException("Choose a valid Endfield game folder first.");
        EnsureGameClosed(game);

        var backupRoot = BackupRoot(game);
        var sources = InspectBackup(backupRoot);
        if (sources.Count == 0) throw new DirectoryNotFoundException("No Format 2 Mods were found in plugin\\backup.");
        var modsRoot = ModService.ModsRoot(game);
        Directory.CreateDirectory(modsRoot);
        ValidateNoConflicts(modsRoot, sources);

        var transactionRoot = Path.Combine(modsRoot, ".effmm-migration-" + Guid.NewGuid().ToString("N"));
        var keepStaging = false;
        try
        {
            var (stagingRoot, output) = await BuildStagedBatchAsync(
                game, resourceRoot, workspace, backupRoot, sources, transactionRoot, progress, token);
            progress?.Report("Publishing the verified Mod batch...");
            PublishBatch(modsRoot, stagingRoot, sources, () => keepStaging = true);
            return output;
        }
        finally
        {
            if (!keepStaging && Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, true);
        }
    }

    internal static async Task<LegacyMigrationSummary> MigrateInstalledModsAsync(string game, string resourceRoot,
        string workspace, IReadOnlyList<ModEntry> mods, IProgress<string>? progress, CancellationToken token)
    {
        game = Path.GetFullPath(game);
        if (!GameDiscovery.IsGame(game)) throw new IOException("Choose a valid Endfield game folder first.");
        EnsureGameClosed(game);
        var selected = mods.Where(mod => mod.Format == "2").ToArray();
        if (selected.Length == 0) return new LegacyMigrationSummary(0, 0, []);

        var duplicate = selected.GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException("Selected Format 2 Mods need unique, nonempty IDs: " + duplicate.Key);

        var modsRoot = ModService.ModsRoot(game);
        var transactionRoot = Path.Combine(modsRoot, ".effmm-repair-migration-" + Guid.NewGuid().ToString("N"));
        var inputRoot = Path.Combine(transactionRoot, "mod-inputs");
        var stagingRoot = Path.Combine(transactionRoot, "mods");
        Directory.CreateDirectory(inputRoot);
        var sources = new List<LegacyModSource>(selected.Length);
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keepStaging = false;
        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var mod = selected[index];
                var key = "mod-" + (index + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                var snapshot = Path.Combine(inputRoot, key);
                ModService.CopyDirectorySafely(mod.Folder, snapshot, token);
                if (!mod.IsEnabled)
                {
                    var disabledDescriptor = Path.Combine(snapshot, "mod.ini.effmm-disabled");
                    if (!File.Exists(disabledDescriptor))
                        throw new InvalidDataException("Disabled Format 2 Mod descriptor is missing: " + mod.Folder);
                    File.Move(disabledDescriptor, Path.Combine(snapshot, "mod.ini"));
                    disabled.Add(key);
                }
                CompiledModCache.ClearDerived(snapshot);
                sources.Add(new LegacyModSource(key, mod.Id, mod.Name));
                destinations.Add(key, Path.GetFullPath(mod.Folder));
            }

            var (staged, result) = await BuildStagedBatchAsync(
                game, resourceRoot, workspace, inputRoot, sources, transactionRoot, progress, token);
            PublishReplacements(modsRoot, staged, sources, destinations, disabled,
                () => keepStaging = true);
            return result;
        }
        finally
        {
            if (!keepStaging && Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, true);
        }
    }

    private static async Task<(string StagingRoot, LegacyMigrationSummary Summary)> BuildStagedBatchAsync(
        string game, string resourceRoot, string workspace, string modInputRoot,
        IReadOnlyList<LegacyModSource> sources, string transactionRoot,
        IProgress<string>? progress, CancellationToken token)
    {
        workspace = await ResolveWorkspaceAsync(game, workspace, resourceRoot, progress, token);
        var stagingRoot = Path.Combine(transactionRoot, "mods");
        var sourceRoot = Path.Combine(transactionRoot, "sources");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(sourceRoot);

        progress?.Report("Preparing the Format 3 migration tool...");
        var migrator = await LegacyMigrationTools.GetMigratorAsync(resourceRoot, token);
        var packageRoot = Directory.GetParent(Directory.GetParent(migrator)!.FullName)!.FullName;
        var nativeRoot = Path.Combine(packageRoot, "nativepack");
        var effectPrefabs = await ListEffectPrefabs(migrator, modInputRoot, token);
        progress?.Report("Exporting the exact source Mesh, Material, and Texture records from the AnimeStudio index...");
        var sourceExporter = Path.Combine(nativeRoot, "source-exporter", "eff_migrate_sources.exe");
        await RunSourceExporter(sourceExporter, game, workspace, sourceRoot, modInputRoot, sources, token);
        if (effectPrefabs.Count > 0)
        {
            progress?.Report($"Exporting {effectPrefabs.Count} exact effect source snapshot(s) from the AnimeStudio index...");
            foreach (var prefab in effectPrefabs)
                await RunEffectSourceExporter(sourceExporter, game, workspace, prefab, sourceRoot, token);
        }
        progress?.Report($"Migrating {sources.Count} Format 2 Mods to Format 3...");
        var output = await RunMigrator(migrator, modInputRoot, stagingRoot, workspace, sourceRoot, nativeRoot,
            Path.Combine(packageRoot, "vendor"), sources, token);
        ValidateOutputs(stagingRoot, sources);
        return (stagingRoot, output);
    }

    internal static IReadOnlyList<string> ParseEffectPrefabs(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("effects", out var effects) || effects.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The migration tool returned an invalid effect list.");

        var prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var effect in effects.EnumerateArray())
        {
            if (!effect.TryGetProperty("prefab", out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The migration tool returned an effect without a Prefab path.");
            prefabs.Add(ValidateEffectPrefab(value.GetString()));
        }
        return prefabs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ValidateEffectPrefab(string? prefab)
    {
        if (string.IsNullOrWhiteSpace(prefab))
            throw new InvalidDataException("The migration tool returned an empty effect Prefab path.");
        var normalized = prefab.Replace('\\', '/');
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith('/') || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".." || segment.Contains(':') ||
                segment.Any(invalidFileNameChars.Contains)))
            throw new InvalidDataException("The migration tool returned an invalid effect Prefab path.");
        return normalized;
    }

    private static async Task<IReadOnlyList<string>> ListEffectPrefabs(string migrator, string backupRoot,
        CancellationToken token)
    {
        var start = new ProcessStartInfo(migrator)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(migrator)!
        };
        start.ArgumentList.Add("--list-effect-prefabs");
        start.ArgumentList.Add("--backup-root");
        start.ArgumentList.Add(backupRoot);

        using var process = Process.Start(start) ?? throw new IOException("Could not inspect legacy effect references.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new IOException("Could not inspect legacy effect references; no Mod output was published.\n" +
                Truncate(stdout + Environment.NewLine + stderr));
        return ParseEffectPrefabs(stdout);
    }

    private static async Task RunEffectSourceExporter(string executable, string game, string workspace,
        string prefab, string outputRoot, CancellationToken token)
    {
        prefab = ValidateEffectPrefab(prefab);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--export-eff-effects");
        start.ArgumentList.Add(game);
        start.ArgumentList.Add(workspace);
        start.ArgumentList.Add(prefab);
        start.ArgumentList.Add(outputRoot);

        using var process = Process.Start(start) ?? throw new IOException("Could not start the AnimeStudio effect source exporter.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new IOException("AnimeStudio could not export an exact effect source snapshot; no Mod output was published.\n" +
                Truncate(stdout + Environment.NewLine + stderr));

        var package = Path.Combine(outputRoot, Path.ChangeExtension(prefab, ".efffx"));
        if (!File.Exists(Path.Combine(package, "effect.json")))
            throw new InvalidDataException("AnimeStudio reported success without writing the effect source snapshot: " + prefab);
    }

    private static async Task RunSourceExporter(string executable, string game, string workspace,
        string outputRoot, string backupRoot, IReadOnlyList<LegacyModSource> sources, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--export-eff-migration-sources");
        start.ArgumentList.Add(game);
        start.ArgumentList.Add(workspace);
        start.ArgumentList.Add(outputRoot);
        foreach (var source in sources)
        {
            start.ArgumentList.Add("--mod-directory");
            start.ArgumentList.Add(Path.Combine(backupRoot, source.FolderName));
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start the AnimeStudio migration source exporter.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new IOException("AnimeStudio could not export every required source record; no Mod output was published.\n" +
                Truncate(stdout + Environment.NewLine + stderr));
    }

    internal static void PublishBatch(string modsRoot, string stagingRoot, IReadOnlyList<LegacyModSource> sources,
        Action? preserveStagingOnRollbackFailure = null)
    {
        ValidateOutputs(stagingRoot, sources);
        ValidateNoConflicts(modsRoot, sources);

        var published = new List<(string Staged, string Destination)>();
        try
        {
            foreach (var source in sources)
            {
                var staged = Path.Combine(stagingRoot, source.FolderName);
                var destination = Path.Combine(modsRoot, source.FolderName);
                Directory.Move(staged, destination);
                published.Add((staged, destination));
            }
        }
        catch (Exception publishError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var item in published.AsEnumerable().Reverse())
            {
                try { Directory.Move(item.Destination, item.Staged); }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
            {
                preserveStagingOnRollbackFailure?.Invoke();
                throw new IOException("Batch publication failed and rollback was incomplete. Staging data was retained at: " + stagingRoot,
                    new AggregateException(new[] { publishError }.Concat(rollbackErrors)));
            }
            throw new IOException("Batch publication failed. All published Mods were rolled back.", publishError);
        }
    }

    internal static void PublishReplacements(string modsRoot, string stagingRoot,
        IReadOnlyList<LegacyModSource> sources, IReadOnlyDictionary<string, string> destinations,
        IReadOnlySet<string> disabled, Action? preserveStagingOnRollbackFailure = null)
    {
        ValidateOutputs(stagingRoot, sources);
        var root = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var backupRoot = Path.Combine(modsRoot, ".eff-repair-backups", "format2-migration",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        var moved = new List<(string Staged, string Destination, string Backup)>();
        try
        {
            foreach (var source in sources)
            {
                if (!destinations.TryGetValue(source.FolderName, out var destination))
                    throw new InvalidDataException("A migrated Mod has no replacement destination: " + source.FolderName);
                destination = Path.GetFullPath(destination);
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(destination))
                    throw new IOException("The selected Mod folder moved or is outside plugin\\mods: " + destination);

                var staged = Path.Combine(stagingRoot, source.FolderName);
                if (disabled.Contains(source.FolderName))
                    File.Move(Path.Combine(staged, "mod.ini"), Path.Combine(staged, "mod.ini.effmm-disabled"));
                var backup = Path.Combine(backupRoot, source.FolderName);
                Directory.Move(destination, backup);
                try
                {
                    Directory.Move(staged, destination);
                }
                catch
                {
                    Directory.Move(backup, destination);
                    throw;
                }
                moved.Add((staged, destination, backup));

                var installed = ModService.ScanTree(destination).SingleOrDefault();
                if (installed is null || installed.Format != "3" ||
                    !string.Equals(installed.Id, source.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The published replacement failed Format 3 validation: " + source.FolderName);
            }
        }
        catch (Exception publishError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(item.Destination)) Directory.Move(item.Destination, item.Staged);
                    if (Directory.Exists(item.Backup)) Directory.Move(item.Backup, item.Destination);
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
            {
                preserveStagingOnRollbackFailure?.Invoke();
                throw new IOException("Format 3 replacement failed and rollback was incomplete. Recovery data was retained at: " + stagingRoot,
                    new AggregateException(new[] { publishError }.Concat(rollbackErrors)));
            }
            throw new IOException("Format 3 replacement failed. Original Mods were restored.", publishError);
        }
    }

    private static void ValidateNoConflicts(string modsRoot, IReadOnlyList<LegacyModSource> sources)
    {
        var ids = sources.Select(source => source.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicate = ModService.ScanTree(modsRoot).FirstOrDefault(mod => !string.IsNullOrWhiteSpace(mod.Id) && ids.Contains(mod.Id));
        if (duplicate is not null) throw new IOException("A Mod with this ID is already installed: " + duplicate.Id);
        foreach (var source in sources)
        {
            var destination = Path.Combine(modsRoot, source.FolderName);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("A destination Mod folder already exists: " + source.FolderName);
        }
    }

    private static void ValidateOutputs(string stagingRoot, IReadOnlyList<LegacyModSource> sources)
    {
        var expectedNames = sources.Select(source => source.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputNames = Directory.EnumerateDirectories(stagingRoot).Select(Path.GetFileName).ToArray();
        if (outputNames.Length != expectedNames.Count || outputNames.Any(name => name is null || !expectedNames.Contains(name)))
            throw new InvalidDataException("The migration tool did not produce exactly one output folder per backup Mod.");
        foreach (var source in sources)
        {
            var output = Path.Combine(stagingRoot, source.FolderName);
            var mods = ModService.ScanTree(output);
            if (mods.Count != 1 || !string.Equals(Path.GetFullPath(mods[0].Folder), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase) ||
                mods[0].Format != "3" || !string.Equals(mods[0].Id, source.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The migrated output failed Format 3 identity validation: " + source.FolderName);
        }
    }

    private static async Task<LegacyMigrationSummary> RunMigrator(string executable, string backupRoot, string stagingRoot,
        string workspace, string sourceRoot, string nativeRoot, string vendorRoot,
        IReadOnlyList<LegacyModSource> sources, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--backup-root"); start.ArgumentList.Add(backupRoot);
        start.ArgumentList.Add("--destination-root"); start.ArgumentList.Add(stagingRoot);
        start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(workspace);
        start.ArgumentList.Add("--source-root"); start.ArgumentList.Add(sourceRoot);
        start.ArgumentList.Add("--author-core"); start.ArgumentList.Add(Path.Combine(nativeRoot, "eff_author_core.dll"));
        start.ArgumentList.Add("--author-assets"); start.ArgumentList.Add(Path.Combine(nativeRoot, "eff_author_assets.dll"));
        start.ArgumentList.Add("--texconv"); start.ArgumentList.Add(Path.Combine(nativeRoot, "texconv.exe"));
        start.ArgumentList.Add("--vendor"); start.ArgumentList.Add(vendorRoot);
        foreach (var source in sources)
        {
            start.ArgumentList.Add("--mod");
            start.ArgumentList.Add(source.FolderName);
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start the Format 3 migration tool.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new IOException("Format 3 migration failed; no output was published.\n" + Truncate(stdout + Environment.NewLine + stderr));

        var migrated = new List<JsonElement>();
        foreach (var line in stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                if (row.TryGetProperty("status", out var status) && status.GetString() == "migrated")
                    migrated.Add(row.Clone());
            }
            catch (JsonException) { }
        }
        if (migrated.Count != sources.Count)
            throw new InvalidDataException("The migration tool's success report does not match the requested batch.");
        var effects = migrated.Sum(row => row.TryGetProperty("migratedEffects", out var count) ? count.GetInt32() : 0);
        return new LegacyMigrationSummary(migrated.Count, effects,
            migrated.Select(row => row.GetProperty("modId").GetString() ?? "").ToArray());
    }

    private static string Truncate(string value) => value.Length <= 12000 ? value : value[^12000..];

    private static void EnsureGameClosed(string game)
    {
        foreach (var process in Process.GetProcessesByName("Endfield"))
        {
            using (process)
            {
                string path;
                try { path = process.MainModule?.FileName ?? ""; }
                catch { throw new IOException("Close Endfield before migrating Mods."); }
                if (string.Equals(Path.GetDirectoryName(path), game, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Close Endfield before migrating Mods.");
            }
        }
    }
}
