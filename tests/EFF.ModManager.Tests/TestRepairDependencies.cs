using System.Threading;
using EffModManager;

namespace AnimeStudio.GUI;

internal sealed class EndfieldVfsArchive
{
    internal static string CurrentContentIdentity { get; set; } = string.Empty;
    internal string? GameDirectory { get; init; }
    internal string VfsRoot { get; init; } = string.Empty;
    internal string ContentIdentity { get; init; } = CurrentContentIdentity;

    internal static EndfieldVfsArchive Open(string game) => new() { GameDirectory = game, VfsRoot = game };
    internal void VerifyCurrent() { }
}

internal static class EndfieldVfsAssetIndexBuilder
{
    internal static bool TryGetOrRebindCurrentIndex(EndfieldVfsArchive archive, string workspace, out string indexPath,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        indexPath = Path.Combine(workspace, "index", "endfield_assets.eidx");
        return File.Exists(indexPath);
    }

    internal static bool TryGetCurrentIndex(EndfieldVfsArchive archive, string workspace, out string indexPath)
    {
        indexPath = Path.Combine(workspace, "index", "endfield_assets.eidx");
        return File.Exists(indexPath);
    }
}

internal static class EndfieldSharedIndexes
{
    internal static string? CurrentWorkspace { get; set; }
    internal static string? RememberedWorkspace { get; set; }
    internal static string Game(EndfieldVfsArchive archive) => archive.GameDirectory ?? archive.VfsRoot;
    internal static string WorkspacePath(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return string.Empty;
        var path = Path.GetFullPath(location);
        if (File.Exists(path) && Path.GetFileName(path).Equals("endfield_assets.eidx", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(path)?.Parent?.FullName ?? string.Empty;
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("index", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(path)?.FullName ?? string.Empty : path;
    }
    internal static IEnumerable<string> Workspaces(string game)
    {
        var path = Path.Combine(SharedResources.MetadataRoot, "registry.json");
        if (!File.Exists(path)) return [];
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("entries").EnumerateArray()
                .Where(entry => string.Equals(entry.GetProperty("game").GetString(), game, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.GetProperty("workspace").GetString() ?? string.Empty)
                .Where(value => value.Length > 0).ToArray();
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { return []; }
    }
    internal static string DefaultWorkspace(string game, string resourceRoot) =>
        Path.Combine(resourceRoot, "indexes", "test-workspace");
    internal static string? FindCurrent(EndfieldVfsArchive archive, CancellationToken token, params string[] additional)
    {
        token.ThrowIfCancellationRequested();
        return CurrentWorkspace;
    }
    internal static bool TryRemember(EndfieldVfsArchive archive, string workspace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RememberedWorkspace = workspace;
        return true;
    }
}

internal static class EndfieldPortableIndex
{
    internal static string ContentIdentity(EndfieldVfsArchive archive) => archive.ContentIdentity;
    internal static string Import(EndfieldVfsArchive archive, string workspace, string package,
        string expectedContent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expectedContent != archive.ContentIdentity) throw new InvalidDataException("Content mismatch");
        var indexPath = Path.Combine(workspace, "index", "endfield_assets.eidx");
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        File.WriteAllText(indexPath, "imported");
        File.WriteAllText(indexPath + ".bundles", "imported");
        return indexPath;
    }
}
