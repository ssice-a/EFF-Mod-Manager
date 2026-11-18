using System.Text.Json;

namespace EffModManager;

internal sealed record CatalogData<T>(T Value, string IndexPath = "");

// A presentation cache, never evidence that an author replacement is safe.
// Hits use file metadata only. Applying a replacement still validates the actual game/index.
internal static class GameCatalogCache
{
    private sealed record Stamp(string Path, long Length, long Modified);
    private sealed record Saved<T>(string Format, string Game, string Selection, Stamp[] GameFiles, Stamp[] IndexFiles, CatalogData<T> Data);
    private const string Format = "EFF_MANAGER_CATALOG/1";

    internal static CatalogData<T> Load<T>(string game, string resources, string name, string selectedWorkspace,
        Func<CatalogData<T>> build, CancellationToken token, bool refresh = false)
    {
        game = Path.GetFullPath(game);
        var root = string.IsNullOrWhiteSpace(resources) ? Path.Combine(game, "plugin") : resources;
        var path = Path.Combine(root, ".effmm-cache", "catalogs", SettingsStore.GameKey(game), name + ".json");
        var before = GameFiles(game);
        token.ThrowIfCancellationRequested();
        if (!refresh && File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Catalog cache is too large: " + path);
                var saved = JsonSerializer.Deserialize<Saved<T>>(File.ReadAllText(path));
                if (saved is not null && saved.Format == Format && saved.Game == game && saved.Selection == selectedWorkspace &&
                    saved.GameFiles.SequenceEqual(before) && saved.IndexFiles.SequenceEqual(IndexFiles(saved.Data.IndexPath)))
                    return saved.Data;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
            { ActivityLog.Default.Failure("log.catalog", error, warning: true); }
        }
        var result = build();
        token.ThrowIfCancellationRequested();
        var after = GameFiles(game);
        if (!before.SequenceEqual(after)) throw new IOException("Game files changed while the catalog was being built. Retry after the game update finishes.");
        try
        {
            SettingsStore.WriteAtomic(path, JsonSerializer.Serialize(new Saved<T>(Format, game, selectedWorkspace,
                after, IndexFiles(result.IndexPath), result)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ActivityLog.Default.Failure("log.catalog", error, warning: true); }
        return result;
    }

    private static Stamp Stat(string path)
    {
        var info = new FileInfo(path);
        return new(Path.GetFullPath(path), info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
    }

    private static Stamp[] GameFiles(string game)
    {
        var roots = new[] { Path.Combine(game, "Endfield_Data", "StreamingAssets", "VFS"),
            Path.Combine(game, "Endfield_Data", "Persistent", "VFS") };
        if (!roots.Any(Directory.Exists)) roots = [game];
        // BLC file additions/removals/changes invalidate names. No VFS block or .eidx is opened on a hit.
        return roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*.blc", SearchOption.AllDirectories))
            .Append(SharedResources.SettingsPath).Append(Path.Combine(SharedResources.MetadataRoot, "registry.json"))
            .Order(StringComparer.OrdinalIgnoreCase).Select(Stat).ToArray();
    }

    private static Stamp[] IndexFiles(string path) => path.Length == 0 ? [] : new[]
    {
        Stat(path), Stat(path + ".bundles"), Stat(Path.Combine(Path.GetDirectoryName(path)!, "endfield-index-manifest.json"))
    };
}
