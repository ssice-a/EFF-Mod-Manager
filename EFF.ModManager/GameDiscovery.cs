using Microsoft.Win32;
using System.Diagnostics;

namespace EffModManager;

internal static class GameDiscovery
{
    internal static bool IsGame(string? path) => !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "Endfield.exe")) &&
        File.Exists(Path.Combine(path, "GameAssembly.dll")) &&
        Directory.Exists(Path.Combine(path, "Endfield_Data"));

    internal static List<string> Find(string? saved)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && IsGame(path))
                results.Add(Path.GetFullPath(path));
        }

        Add(saved);
        foreach (var process in Process.GetProcessesByName("Endfield"))
        {
            using (process)
            {
                try { Add(Path.GetDirectoryName(process.MainModule?.FileName)); }
                catch { }
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            Add(Path.Combine(drive.RootDirectory.FullName, "Hypergryph Launcher", "games", "Endfield Game"));
            Add(Path.Combine(drive.RootDirectory.FullName, "Games", "Endfield Game"));
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var uninstall = hive.OpenSubKey(key);
            if (uninstall is null) continue;
            foreach (var child in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(child);
                var name = entry?.GetValue("DisplayName") as string ?? "";
                if (name.Contains("Endfield", StringComparison.OrdinalIgnoreCase) || name.Contains("终末地", StringComparison.Ordinal))
                    Add(entry?.GetValue("InstallLocation") as string);
            }
        }

        return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
