using System.Text.Json;

namespace EffModManager;

internal static class SettingsStore
{
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EFF-Mod-Manager");
    internal static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    internal static string DllStatePath(string game) => Path.Combine(DataRoot, "installations", GameKey(game), "dll-install.json");
    internal static string BackupRoot(string game) => Path.Combine(DataRoot, "installations", GameKey(game), "backups");
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
