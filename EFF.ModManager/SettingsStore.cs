using System.Text.Json;

namespace EffModManager;

internal static class SettingsStore
{
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EFF-Mod-Manager");
    internal static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    internal static string DllStatePath(string game) => Path.Combine(DataRoot, "installations", GameKey(game), "dll-install.json");
    internal static string BackupRoot(string game) => Path.Combine(DataRoot, "installations", GameKey(game), "backups");
    internal static bool IsSystemDrivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = Environment.GetEnvironmentVariable("SystemDrive") + Path.DirectorySeparatorChar;
        var pathRoot = Path.GetPathRoot(Path.GetFullPath(path));
        return !string.IsNullOrWhiteSpace(systemRoot) &&
            string.Equals(pathRoot, systemRoot, StringComparison.OrdinalIgnoreCase);
    }
    internal static string RequireNonSystemCacheRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("Choose a cache folder outside the Windows system drive.");
        var fullPath = Path.GetFullPath(path);
        if (IsSystemDrivePath(fullPath))
            throw new IOException("Cache files must be stored outside the Windows system drive.");
        return fullPath;
    }
    internal static string GameStagingPath(string game, string operation) => Path.Combine(
        Path.GetFullPath(game), "plugin", ".effmm-staging-" + operation + "-" + Guid.NewGuid().ToString("N"));
    internal static bool IsLegacyResourceRoot(string? resourceRoot, string legacyRoot) =>
        !string.IsNullOrWhiteSpace(resourceRoot) &&
        string.Equals(Path.GetFullPath(resourceRoot), Path.GetFullPath(legacyRoot), StringComparison.OrdinalIgnoreCase);
    internal static string GameKey(string game) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant())))[..16];
    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    internal static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new AppSettings(); }
    }

    internal static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataRoot);
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}
