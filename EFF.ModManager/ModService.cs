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
        var name = First(Value("name"), Value("title"), Path.GetFileName(folder), id);
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
            Format = Value("format"),
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
        var temporary = SettingsStore.GameStagingPath(game, "import");
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

    internal static List<ModEntry> ScanTree(string root)
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

    internal static void RequireFormat3(string mod)
    {
        var entries = ScanTree(mod);
        if (entries.Count != 1)
            throw new IOException("Select one Mod folder with one mod.ini before rebuilding its runtime cache.");
        if (entries[0].Format == "2")
            throw new IOException("This is a Format 2 Mod. Migrate it to Format 3 from the Repair page before rebuilding its runtime cache.");
        if (entries[0].Format != "3")
            throw new IOException("Only Format 3 Mods can be repaired for the current runtime.");
    }

    internal static void CopyDirectorySafely(string source, string destination, CancellationToken token)
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

    internal static void Rename(string modsRoot, ModEntry mod, string requestedName, IReadOnlyList<ModEntry> allMods)
    {
        var name = ValidateModName(requestedName);
        var root = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var oldFolder = Path.GetFullPath(mod.Folder);
        var relativeRoot = Path.GetRelativePath(root, oldFolder);
        if (relativeRoot == "." || Path.IsPathRooted(relativeRoot) || relativeRoot == ".." ||
            relativeRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativeRoot.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The Mod folder must be inside the game's Mods directory and cannot be the Mods directory itself.");

        var iniPath = Path.GetFullPath(mod.IniPath);
        if (!string.Equals(Path.GetDirectoryName(iniPath), oldFolder, StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(Path.GetFileName(iniPath), "mod.ini", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(Path.GetFileName(iniPath), "mod.ini" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The Mod descriptor is not directly inside its Mod folder.");

        var newFolder = Path.Combine(Path.GetDirectoryName(oldFolder)!, name);
        var sameFolderIgnoringCase = string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase);
        if (!sameFolderIgnoringCase && (Directory.Exists(newFolder) || File.Exists(newFolder)))
            throw new IOException("A file or Mod folder with that name already exists.");

        var originalIni = File.ReadAllBytes(iniPath);
        var updatedIni = UpdateModIniName(originalIni, name);
        var iniChanged = !originalIni.AsSpan().SequenceEqual(updatedIni);
        if (iniChanged) WriteBytesAtomic(iniPath, updatedIni);

        try
        {
            if (!string.Equals(oldFolder, newFolder, StringComparison.Ordinal))
                MoveModFolder(oldFolder, newFolder, root, sameFolderIgnoringCase);
        }
        catch (Exception moveError)
        {
            if (iniChanged)
            {
                try { WriteBytesAtomic(iniPath, originalIni); }
                catch (Exception rollbackError)
                {
                    throw new IOException("Mod folder rename failed and the mod.ini change could not be rolled back.",
                        new AggregateException(moveError, rollbackError));
                }
            }
            throw;
        }

        var oldPrefix = oldFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var affected = allMods.Append(mod).Distinct().Where(item =>
        {
            var folder = Path.GetFullPath(item.Folder);
            return string.Equals(folder, oldFolder, StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        foreach (var item in affected)
        {
            var itemFolder = Path.GetFullPath(item.Folder);
            var itemIni = Path.GetFullPath(item.IniPath);
            var relativeFolder = Path.GetRelativePath(oldFolder, itemFolder);
            var relativeIni = Path.GetRelativePath(oldFolder, itemIni);
            item.Folder = relativeFolder == "." ? newFolder : Path.GetFullPath(Path.Combine(newFolder, relativeFolder));
            item.IniPath = Path.GetFullPath(Path.Combine(newFolder, relativeIni));
        }
        mod.Name = name;
    }

    private static string ValidateModName(string requestedName)
    {
        var name = requestedName.Trim();
        if (name.Length == 0) throw new IOException("Mod name cannot be empty.");
        if (name.Length > 80) throw new IOException("Mod name cannot exceed 80 characters.");
        if (name.Any(character => char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character)) || name.EndsWith('.'))
            throw new IOException("Mod name contains characters that are not allowed in a folder name.");
        var deviceName = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(deviceName, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Mod name is reserved by Windows and cannot be used as a folder name.");
        return name;
    }

    private sealed class IniLine(string content, string ending)
    {
        internal string Content { get; set; } = content;
        internal string Ending { get; set; } = ending;
    }

    private static byte[] UpdateModIniName(byte[] source, string name)
    {
        var (encoding, preambleLength) = DetectEncoding(source);
        var text = encoding.GetString(source, preambleLength, source.Length - preambleLength);
        var lines = SplitIniLines(text);
        var currentSection = "";
        var nameLines = new List<int>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Content.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }
            if (string.Equals(currentSection, "Mod", StringComparison.OrdinalIgnoreCase) && IsDisplayNameEntry(lines[index].Content))
                nameLines.Add(index);
        }

        if (nameLines.Count == 0) return source;
        foreach (var index in nameLines) SetIniValue(lines[index], name);

        var updatedText = string.Concat(lines.Select(line => line.Content + line.Ending));
        var payload = encoding.GetBytes(updatedText);
        var preamble = encoding.GetPreamble();
        var result = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, result, preamble.Length, payload.Length);
        return result;
    }

    private static bool IsDisplayNameEntry(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0) return false;
        var key = line[..separator].Trim();
        return string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "title", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetIniValue(IniLine line, string value)
    {
        var separator = line.Content.IndexOf('=');
        var valueStart = separator + 1;
        while (valueStart < line.Content.Length && char.IsWhiteSpace(line.Content[valueStart])) valueStart++;
        var suffixStart = line.Content.Length;
        while (suffixStart > valueStart && char.IsWhiteSpace(line.Content[suffixStart - 1])) suffixStart--;
        line.Content = line.Content[..valueStart] + value + line.Content[suffixStart..];
    }

    private static List<IniLine> SplitIniLines(string text)
    {
        var result = new List<IniLine>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not '\r' and not '\n') continue;
            var endingLength = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            result.Add(new IniLine(text[start..index], text.Substring(index, endingLength)));
            index += endingLength - 1;
            start = index + 1;
        }
        if (start < text.Length || result.Count == 0) result.Add(new IniLine(text[start..], ""));
        return result;
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(true, true, true), 4);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (new UTF32Encoding(false, true, true), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true, true), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, true, true), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, true, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    private static void MoveModFolder(string source, string destination, string modsRoot, bool caseOnly)
    {
        if (!caseOnly)
        {
            Directory.Move(source, destination);
            return;
        }

        var temporary = Path.Combine(modsRoot, ".effmm-rename-" + Guid.NewGuid().ToString("N"));
        Directory.Move(source, temporary);
        try { Directory.Move(temporary, destination); }
        catch (Exception moveError)
        {
            try { Directory.Move(temporary, source); }
            catch (Exception rollbackError)
            {
                throw new IOException("Mod folder casing update failed and the original folder could not be restored.",
                    new AggregateException(moveError, rollbackError));
            }
            throw;
        }
    }

    private static void WriteBytesAtomic(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
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
