using System.Collections.ObjectModel;

namespace EffModManager;

internal sealed class AppSettings
{
    public string GamePath { get; set; } = "";
    public string ResourceRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EFF", "Resources");
    public bool English { get; set; }
}

internal sealed class ModEntry
{
    public string Folder { get; init; } = "";
    public string IniPath { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsEnabled { get; init; }
    public bool IsRepairSelected { get; set; } = true;
    public ObservableCollection<string> Hotkeys { get; init; } = [];
    public override string ToString() => Name;
}

internal sealed record RepairRunSummary(int Repaired, int Failed, string Workspace);

internal sealed class DllInstallationState
{
    public string Version { get; set; } = "";
    public string InstalledAt { get; set; } = "";
    public List<DllFileRecord> Files { get; set; } = [];
}

internal sealed class DllFileRecord
{
    public string RelativePath { get; set; } = "";
    public string InstalledHash { get; set; } = "";
    public string? OriginalBackup { get; set; }
    public string? OriginalHash { get; set; }
}

internal sealed class GitHubRelease
{
    public string tag_name { get; set; } = "";
    public string name { get; set; } = "";
    public bool prerelease { get; set; }
    public List<GitHubAsset> assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    public string name { get; set; } = "";
    public string browser_download_url { get; set; } = "";
    public long size { get; set; }
    public string? digest { get; set; }
}
