using System.Threading;

namespace AnimeStudio.GUI;

internal sealed class EndfieldVfsArchive
{
    internal string? GameDirectory { get; init; }
    internal string VfsRoot { get; init; } = string.Empty;
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
