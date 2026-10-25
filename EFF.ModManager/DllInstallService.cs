using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace EffModManager;

internal static class DllInstallService
{
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "plugin/eff.dll", "plugin/eff.ini", "plugin/package-protocol.json", "plugin/LICENSE", "plugin/THIRD_PARTY_NOTICES",
        "d3dcompiler_47.dll", "vulkan-1.dll"
    };

    internal static DllInstallationState? ReadState(string game)
    {
        var path = SettingsStore.DllStatePath(game);
        try { return JsonSerializer.Deserialize<DllInstallationState>(File.ReadAllText(path), SettingsStore.JsonOptions); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static bool IsManaged(string game) => ReadState(game) is { Files.Count: > 0 };

    internal static Version? InstalledVersion(string game)
    {
        var dll = Path.Combine(game, "plugin", "eff.dll");
        if (File.Exists(dll))
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            var fileVersion = NormalizeVersion(info.ProductVersion) ?? NormalizeVersion(info.FileVersion);
            if (fileVersion is not null) return fileVersion;
        }
        return NormalizeVersion(ReadState(game)?.Version);
    }

    private static Version? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().TrimStart('v', 'V');
        var suffix = value.IndexOfAny([' ', '+', '-']);
        if (suffix >= 0) value = value[..suffix];
        if (!Version.TryParse(value, out var version)) return null;
        return new Version(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
    }

    internal static async Task<Version?> GetLatestVersionAsync(CancellationToken token)
    {
        var asset = await GitHubReleaseService.GetLatestDllAssetAsync(token);
        return asset is null ? null : GitHubReleaseService.DllVersion(asset.name);
    }

    internal static async Task<Version> InstallOrUpdateAsync(string game, IProgress<string>? progress, CancellationToken token)
    {
        if (!GameDiscovery.IsGame(game)) throw new IOException("Select a valid Endfield game folder.");
        RequireGameClosed(game);
        var asset = await GitHubReleaseService.GetLatestDllAssetAsync(token)
            ?? throw new IOException("The latest EFF Release has no DLL package.");
        var version = GitHubReleaseService.DllVersion(asset.name);
        var state = ReadState(game) ?? new DllInstallationState();
        foreach (var record in state.Files) ValidateManagedPath(record.RelativePath);
        var previous = state.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var temp = Path.Combine(SettingsStore.DataRoot, "staging", "dll-" + Guid.NewGuid().ToString("N"));
        var rollback = Path.Combine(temp, "rollback");
        var extracted = Path.Combine(temp, "package");
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(rollback);
        var archivePath = Path.Combine(temp, asset.name);
        var changed = new List<(string Target, string? Snapshot)>();
        var statePath = SettingsStore.DllStatePath(game);
        try
        {
            progress?.Report("Downloading " + asset.name);
            await GitHubReleaseService.DownloadVerifiedAsync(asset, archivePath, progress, token);
            await ExtractPackageAsync(archivePath, extracted, token);
            var sourceFiles = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories)
                .ToDictionary(path => ToRelative(extracted, path), path => path, StringComparer.OrdinalIgnoreCase);
            if (!sourceFiles.ContainsKey("plugin/eff.dll") || !sourceFiles.ContainsKey("plugin/package-protocol.json"))
                throw new InvalidDataException("The DLL package is incomplete: eff.dll or package protocol is missing.");

            var root = Path.GetFullPath(game);
            var next = new List<DllFileRecord>();
            var wanted = new HashSet<string>(sourceFiles.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var old in state.Files.Where(old => !wanted.Contains(old.RelativePath)))
                await RestoreOldFileAsync(old, root, rollback, changed, token);

            foreach (var pair in sourceFiles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var relative = pair.Key;
                var target = SafeCombine(root, relative);
                previous.TryGetValue(relative, out var oldRecord);
                if (relative.Equals("plugin/eff.ini", StringComparison.OrdinalIgnoreCase) && oldRecord is null && File.Exists(target))
                    continue;
                if (relative.Equals("plugin/eff.ini", StringComparison.OrdinalIgnoreCase) && oldRecord is not null)
                {
                    next.Add(oldRecord);
                    continue;
                }

                var currentHash = File.Exists(target) ? Hash(target) : null;
                if (oldRecord is not null && currentHash is not null && !string.Equals(currentHash, oldRecord.InstalledHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The installed file was changed outside the manager; refusing to overwrite it: {relative}");

                var originalBackup = oldRecord?.OriginalBackup;
                var originalHash = oldRecord?.OriginalHash;
                if (oldRecord is null && File.Exists(target))
                {
                    var backupRoot = SettingsStore.BackupRoot(game);
                    Directory.CreateDirectory(backupRoot);
                    var backupName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative.ToLowerInvariant()))) + ".bak";
                    var backupPath = Path.Combine(backupRoot, backupName);
                    File.Copy(target, backupPath, true);
                    originalBackup = Path.GetFileName(backupPath);
                    originalHash = currentHash;
                }

                var extractedFile = pair.Value;
                var installedHash = Hash(extractedFile);
                if (!string.Equals(currentHash, installedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var snapshot = File.Exists(target) ? Path.Combine(rollback, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative))) + ".bak") : null;
                    if (snapshot is not null) File.Copy(target, snapshot, true);
                    changed.Add((target, snapshot));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var stagingFile = target + ".effmm-" + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(extractedFile, stagingFile, true);
                    File.Move(stagingFile, target, true);
                }
                next.Add(new DllFileRecord { RelativePath = relative, InstalledHash = installedHash, OriginalBackup = originalBackup, OriginalHash = originalHash });
            }

            token.ThrowIfCancellationRequested();
            var newState = new DllInstallationState { Version = version.ToString(), InstalledAt = DateTimeOffset.UtcNow.ToString("O"), Files = next };
            SettingsStore.WriteAtomic(statePath, JsonSerializer.Serialize(newState, SettingsStore.JsonOptions));
            return version;
        }
        catch
        {
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                if (item.Snapshot is null)
                {
                    if (File.Exists(item.Target)) File.Delete(item.Target);
                }
                else if (File.Exists(item.Snapshot)) File.Copy(item.Snapshot, item.Target, true);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    internal static string Uninstall(string game)
    {
        if (!GameDiscovery.IsGame(game)) throw new IOException("Select a valid Endfield game folder.");
        RequireGameClosed(game);
        var state = ReadState(game) ?? throw new IOException("No EFF plugin installation managed by this app was found.");
        foreach (var record in state.Files) ValidateManagedPath(record.RelativePath);
        var root = Path.GetFullPath(game);
        var planned = new List<(DllFileRecord Record, string Target, string? RestoreFrom)>();
        var changed = new List<string>();
        foreach (var file in state.Files)
        {
            var target = SafeCombine(root, file.RelativePath);
            if (File.Exists(target) && !string.Equals(Hash(target), file.InstalledHash, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(file.RelativePath);
                continue;
            }
            string? restoreFrom = null;
            if (file.OriginalBackup is { Length: > 0 } backupName)
            {
                var backup = Path.Combine(SettingsStore.BackupRoot(game), Path.GetFileName(backupName));
                if (!File.Exists(backup)) throw new IOException("A pre-install backup is missing; refusing to remove " + file.RelativePath);
                if (file.OriginalHash is { Length: > 0 } expectedHash && !string.Equals(Hash(backup), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A pre-install backup failed its integrity check: " + file.RelativePath);
                restoreFrom = backup;
            }
            if (File.Exists(target) || restoreFrom is not null) planned.Add((file, target, restoreFrom));
        }

        var transaction = Path.Combine(SettingsStore.DataRoot, "staging", "uninstall-" + Guid.NewGuid().ToString("N"));
        var changedTargets = new List<(string Target, string? Snapshot)>();
        Directory.CreateDirectory(transaction);
        var statePath = SettingsStore.DllStatePath(game);
        var stateSnapshot = Path.Combine(transaction, "dll-install.json");
        if (File.Exists(statePath)) File.Copy(statePath, stateSnapshot, true);
        try
        {
            foreach (var item in planned)
            {
                string? snapshot = null;
                if (File.Exists(item.Target))
                {
                    snapshot = Path.Combine(transaction, changedTargets.Count + ".bak");
                    File.Copy(item.Target, snapshot, true);
                }
                changedTargets.Add((item.Target, snapshot));
                if (item.RestoreFrom is null) File.Delete(item.Target);
                else
                {
                    var temp = item.Target + ".effmm-" + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(item.RestoreFrom, temp, true);
                    File.Move(temp, item.Target, true);
                }
            }

            if (File.Exists(statePath)) File.Delete(statePath);
            var plugin = Path.Combine(root, "plugin");
            if (Directory.Exists(plugin) && !Directory.EnumerateFileSystemEntries(plugin).Any()) Directory.Delete(plugin);
            return changed.Count == 0 ? "Removed" : "Preserved modified files: " + string.Join(", ", changed);
        }
        catch
        {
            foreach (var item in changedTargets.AsEnumerable().Reverse())
            {
                if (item.Snapshot is null)
                {
                    if (File.Exists(item.Target)) File.Delete(item.Target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                    File.Copy(item.Snapshot, item.Target, true);
                }
            }
            if (File.Exists(stateSnapshot)) File.Copy(stateSnapshot, statePath, true);
            throw;
        }
        finally
        {
            if (Directory.Exists(transaction)) Directory.Delete(transaction, true);
        }
    }

    private static async Task RestoreOldFileAsync(DllFileRecord old, string root, string rollback,
        List<(string Target, string? Snapshot)> changed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = SafeCombine(root, old.RelativePath);
        if (File.Exists(target) && !string.Equals(Hash(target), old.InstalledHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed file was changed outside the manager; refusing to replace it: " + old.RelativePath);
        string? snapshot = null;
        if (File.Exists(target))
        {
            snapshot = Path.Combine(rollback, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(old.RelativePath))) + ".bak");
            File.Copy(target, snapshot, true);
        }
        if (snapshot is not null || old.OriginalBackup is { Length: > 0 }) changed.Add((target, snapshot));
        if (old.OriginalBackup is { Length: > 0 } backupName)
        {
            var backup = Path.Combine(SettingsStore.BackupRoot(root), Path.GetFileName(backupName));
            if (!File.Exists(backup)) throw new IOException("A pre-install backup is missing; refusing to remove " + old.RelativePath);
            if (old.OriginalHash is { Length: > 0 } expectedHash && !string.Equals(Hash(backup), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A pre-install backup failed its integrity check: " + old.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backup, target, true);
        }
        else if (File.Exists(target)) File.Delete(target);
        await Task.CompletedTask;
    }

    private static async Task ExtractPackageAsync(string archivePath, string destination, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 1000) throw new InvalidDataException("EFF package contains too many files.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.EndsWith('/')) continue;
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("EFF package contains a symbolic link.");
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':') || parts.Any(part => part is "." or ".."))
                throw new InvalidDataException("EFF package contains an unsafe file path.");
            if (!AllowedFiles.Contains(normalized)) throw new InvalidDataException("EFF package contains an unexpected file: " + normalized);
            total = checked(total + entry.Length);
            if (total > 200L * 1024 * 1024) throw new InvalidDataException("EFF package exceeds the extraction limit.");
            var target = SafeCombine(destination, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, token);
        }
    }

    private static void RequireGameClosed(string game)
    {
        foreach (var process in Process.GetProcessesByName("Endfield"))
        {
            using (process)
            {
                string path;
                try { path = process.MainModule?.FileName ?? ""; }
                catch { throw new IOException("The game is running. Close it before installing or uninstalling EFF."); }
                if (string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Close the game before installing or uninstalling EFF.");
            }
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        ValidateManagedPath(relative);
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new InvalidDataException("Unsafe managed path in installation state.");
        var target = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed file escapes the selected directory.");
        RejectReparsePoints(root, target);
        return target;
    }

    private static void ValidateManagedPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (!AllowedFiles.Contains(normalized)) throw new InvalidDataException("Unrecognized file in the EFF installation state: " + normalized);
    }

    private static void RejectReparsePoints(string root, string target)
    {
        var current = Path.GetFullPath(root);
        Check(current);
        var relative = Path.GetRelativePath(current, target);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            Check(current);
        }

        static void Check(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing to modify a file or folder link: " + path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string ToRelative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
