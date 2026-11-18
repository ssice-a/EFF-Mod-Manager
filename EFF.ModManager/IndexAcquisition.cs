using AnimeStudio;
using AnimeStudio.GUI;
using System.Net.Http;
using System.Text.Json;

namespace EffModManager;

// One acquisition policy for migration: validate what is already local first,
// then try an exact public index, then build with the bundled source reader.
internal static class IndexAcquisition
{
    internal static async Task<string> ResolveAsync(string game, string selected, string resourceRoot,
        IProgress<string>? progress, CancellationToken token, HttpClient? client = null)
    {
        try { return await ResolveCoreAsync(game, selected, resourceRoot, progress, token, client); }
        catch (Exception error)
        {
            if (error.Data[nameof(RecoveryAdvice)] is null)
                ActivityLog.WithRecovery(error, RecoveryHelp.Index(resourceRoot,
                    EndfieldSharedIndexes.DefaultWorkspace(game, resourceRoot)));
            throw;
        }
    }

    private static async Task<string> ResolveCoreAsync(string game, string selected, string resourceRoot,
        IProgress<string>? progress, CancellationToken token, HttpClient? client)
    {
        var archive = await Task.Run(() => EndfieldVfsArchive.Open(game), token);
        var defaultWorkspace = EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive), resourceRoot);
        var explicitPackage = !string.IsNullOrWhiteSpace(selected) && selected.EndsWith(".effindex.gz", StringComparison.OrdinalIgnoreCase);
        if (explicitPackage && !File.Exists(selected)) throw new FileNotFoundException("The selected portable index package is missing.", selected);
        var existing = explicitPackage ? null : EndfieldSharedIndexes.FindCurrent(archive, token, selected, defaultWorkspace);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        if (string.IsNullOrWhiteSpace(resourceRoot))
            throw new IOException("Choose a resource/index folder before migrating Format 2 Mods.");
        var content = EndfieldPortableIndex.ContentIdentity(archive);
        var workspace = !string.IsNullOrWhiteSpace(selected) && !File.Exists(selected)
            ? Path.GetFullPath(selected) : defaultWorkspace;

        if (File.Exists(selected))
        {
            if (!selected.EndsWith(".effindex.gz", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select an index workspace or a portable .effindex.gz package.");
            progress?.Report("Importing the selected local index package...");
            return Import(archive, workspace, selected, content, token);
        }
        foreach (var package in LocalPackages(resourceRoot, content))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                progress?.Report("Validating a local portable index: " + Path.GetFileName(package));
                return Import(archive, workspace, package, content, token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
            {
                ActivityLog.Default.Failure("log.index", error, RecoveryHelp.Index(resourceRoot, workspace), warning: true);
                progress?.Report("Local index is not compatible; retained the package: " + error.Message);
            }
        }

        try
        {
            progress?.Report("Finding a public index for this game's exact Bundle contents...");
            var published = await EndfieldIndexUpdates.Find(EndfieldIndexUpdates.DefaultSource, content, token, client!);
            if (published is not null && string.Equals(published.Release.GameContent, content, StringComparison.OrdinalIgnoreCase))
            {
                var package = EndfieldIndexUpdates.CachedPackage(published, resourceRoot);
                try { await EndfieldIndexUpdates.Download(published, package, progress!, token, client!); }
                catch (Exception error)
                {
                    throw ActivityLog.WithRecovery(error, RecoveryHelp.Index(resourceRoot, workspace, published.Url));
                }
                return Import(archive, workspace, package, content, token);
            }
            progress?.Report("No exact public index exists; building from the local game.");
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or IOException or TaskCanceledException or InvalidDataException or JsonException)
        {
            ActivityLog.Default.Failure("log.index", error, RecoveryHelp.Index(resourceRoot, workspace), warning: true);
            progress?.Report("Public index unavailable; building from the local game: " + error.Message);
        }
        token.ThrowIfCancellationRequested();
        var buildProgress = new InlineIndexProgress(value => progress?.Report(
            $"Local index: {value.ProcessedBundles}/{value.TotalBundles} Bundles, {value.AssetCount} assets; {value.CurrentPath}"));
        await EndfieldVfsAssetIndexBuilder.BuildAsync(archive, workspace,
            GameManager.GetGameByType(GameType.ArknightsEndfield), buildProgress, token);
        Validate(archive, workspace, token);
        return workspace;
    }

    private static string Import(EndfieldVfsArchive archive, string workspace, string package, string content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        archive.VerifyCurrent();
        EndfieldPortableIndex.Import(archive, workspace, package, content, token);
        Validate(archive, workspace, token);
        return workspace;
    }

    private static void Validate(EndfieldVfsArchive archive, string workspace, CancellationToken token)
    {
        if (!EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out var index, token) || !File.Exists(index + ".bundles"))
            throw new InvalidDataException("Index acquisition did not produce a complete validated workspace.");
        EndfieldSharedIndexes.TryRemember(archive, workspace, token);
    }

    private static IEnumerable<string> LocalPackages(string resourceRoot, string content)
    {
        var roots = new[] { resourceRoot, Path.Combine(resourceRoot, "indexes"),
            Path.Combine(resourceRoot, ".effmm-cache", "indexes", content[21..]) };
        return roots.Where(Directory.Exists).SelectMany(path => Directory.EnumerateFiles(path, "*.effindex.gz", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed class InlineIndexProgress(Action<EndfieldIndexProgress> report) : IProgress<EndfieldIndexProgress>
    {
        public void Report(EndfieldIndexProgress value) => report(value);
    }
}
