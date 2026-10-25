using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EffModManager;

internal static class ModService
{
    private const string DisabledSuffix = ".effmm-disabled";

    internal static string ModsRoot(string game) => Path.Combine(game, "plugin", "mods");

    internal static List<ModEntry> Scan(string game)
    {
        var root = ModsRoot(game);
        if (!Directory.Exists(root)) return [];
        var result = new List<ModEntry>();
        foreach (var directory in WalkDirectories(root))
        {
            var ini = Path.Combine(directory, "mod.ini");
            var disabled = Path.Combine(directory, "mod.ini" + DisabledSuffix);
            var path = File.Exists(ini) ? ini : File.Exists(disabled) ? disabled : null;
            if (path is not null) result.Add(Read(directory, path));
        }
        return result.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<string> WalkDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children.Reverse())
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                try { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
                catch (IOException) { }
            }
        }
    }

    private static ModEntry Read(string folder, string iniPath)
    {
        var sections = ParseIni(File.ReadAllLines(iniPath));
        sections.TryGetValue("Mod", out var metadata);
        metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Value(string key) => metadata.GetValueOrDefault(key, "").Trim();
        var id = Value("id");
        var name = First(Value("name"), Value("title"), id, Path.GetFileName(folder));
        var hotkeys = sections
            .Where(section => section.Key.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && section.Value.TryGetValue("key", out var key) && !string.IsNullOrWhiteSpace(key))
            .Select(section => $"{section.Value["key"].Trim()}  ·  {section.Key}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ModEntry
        {
            Folder = folder,
            IniPath = iniPath,
            Id = id,
            Name = name,
            Author = First(Value("author"), ""),
            Version = First(Value("version"), ""),
            Description = First(Value("description"), Value("summary"), ""),
            IsEnabled = string.Equals(Path.GetFileName(iniPath), "mod.ini", StringComparison.OrdinalIgnoreCase),
            Hotkeys = new System.Collections.ObjectModel.ObservableCollection<string>(hotkeys)
        };
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (name.Length == 0) { current = null; continue; }
                if (!sections.TryGetValue(name, out current)) sections[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0 || current is null) continue;
            current[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return sections;
    }

    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    internal static async Task<string> ImportAsync(string sourcePath, string game, CancellationToken token)
    {
        if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath)) throw new IOException("Import source does not exist.");
        var modsRoot = ModsRoot(game);
        Directory.CreateDirectory(modsRoot);
        var temporary = Path.Combine(SettingsStore.DataRoot, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var stage = Path.Combine(temporary, "package");
        Directory.CreateDirectory(stage);
        var commitStage = Path.Combine(modsRoot, ".effmm-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Directory.Exists(sourcePath)) CopyDirectorySafely(sourcePath, stage, token);
            else await ExtractZipSafelyAsync(sourcePath, stage, token);

            var found = ScanTree(stage).Where(mod => mod.IsEnabled).ToList();
            if (found.Count == 0) throw new InvalidDataException("The folder or ZIP does not contain mod.ini.");
            var duplicatedInside = found.GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicatedInside is not null && !string.IsNullOrWhiteSpace(duplicatedInside.Key))
                throw new InvalidDataException("The package contains duplicate Mod IDs: " + duplicatedInside.Key);
            var installedIds = Scan(game).Select(mod => mod.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicate = found.Select(mod => mod.Id).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id) && installedIds.Contains(id));
            if (duplicate is not null) throw new IOException("A Mod with this ID is already installed: " + duplicate);

            CopyDirectorySafely(stage, commitStage, token);
            var preferredName = Path.GetFileNameWithoutExtension(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (Directory.Exists(sourcePath)) preferredName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var safeName = SanitizeFolderName(First(preferredName, found[0].Name, "Mod"));
            var destination = Path.Combine(modsRoot, safeName);
            for (var suffix = 2; Directory.Exists(destination) || File.Exists(destination); suffix++)
                destination = Path.Combine(modsRoot, safeName + " " + suffix);
            Directory.Move(commitStage, destination);
            var imported = ScanTree(destination).Count(mod => mod.IsEnabled);
            return $"{Path.GetFileName(destination)} ({imported})";
        }
        finally
        {
            if (Directory.Exists(commitStage)) Directory.Delete(commitStage, true);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private static List<ModEntry> ScanTree(string root)
    {
        var entries = new List<ModEntry>();
        foreach (var directory in WalkDirectories(root))
        {
            var enabled = Path.Combine(directory, "mod.ini");
            var disabled = Path.Combine(directory, "mod.ini" + DisabledSuffix);
            if (File.Exists(enabled)) entries.Add(Read(directory, enabled));
            else if (File.Exists(disabled)) entries.Add(Read(directory, disabled));
        }
        return entries;
    }

    private static void CopyDirectorySafely(string source, string destination, CancellationToken token)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links are not supported in Mod folders.");
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links are not supported in Mod folders.");
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            CopyDirectorySafely(directory, Path.Combine(destination, name), token);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links are not supported in Mod folders.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }
    }

    private static async Task ExtractZipSafelyAsync(string archivePath, string destination, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 50000) throw new InvalidDataException("Archive contains too many entries.");
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("Symbolic links are not supported in Mod archives.");
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':')) throw new InvalidDataException("Archive contains an absolute path.");
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment is "." or "..")) throw new InvalidDataException("Archive path escapes the Mod folder.");
            if (segments.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(segments)));
            var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path escapes the Mod folder.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive expands beyond the 2 GiB safety limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, token);
        }
    }

    internal static void SetEnabled(ModEntry mod, bool enabled)
    {
        if (mod.IsEnabled == enabled) return;
        var enabledPath = Path.Combine(mod.Folder, "mod.ini");
        var disabledPath = enabledPath + DisabledSuffix;
        if (enabled)
        {
            if (!File.Exists(disabledPath)) throw new IOException("Disabled Mod descriptor was not found.");
            if (File.Exists(enabledPath)) throw new IOException("Cannot enable because mod.ini already exists.");
            File.Move(disabledPath, enabledPath);
        }
        else
        {
            if (!File.Exists(enabledPath)) throw new IOException("Mod descriptor was not found.");
            if (File.Exists(disabledPath)) throw new IOException("Cannot disable because a disabled descriptor already exists.");
            File.Move(enabledPath, disabledPath);
        }
    }

    internal static void Delete(ModEntry mod, IReadOnlyList<ModEntry> allMods)
    {
        var nested = allMods.FirstOrDefault(other => !string.Equals(other.Folder, mod.Folder, StringComparison.OrdinalIgnoreCase) &&
            other.Folder.StartsWith(mod.Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (nested is not null) throw new IOException("This folder contains a nested Mod. Remove or move the nested package first.");
        Directory.Delete(mod.Folder, true);
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var value = new string(name.Where(c => !invalid.Contains(c) && c is not '\r' and not '\n').ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "Mod" : value.Length > 80 ? value[..80].TrimEnd() : value;
    }
}
