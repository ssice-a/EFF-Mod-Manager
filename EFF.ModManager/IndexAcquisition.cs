using AnimeStudio.GUI;
using System.Net.Http;
using System.Text.Json;

namespace EffModManager;

// Shared acquisition policy for migration and weapon discovery. Reuse local
// indexes and packages, otherwise download an exact match published on GitHub.
internal static class IndexAcquisition
{
    internal static Task<string> ResolveAsync(string game, string selected, string resourceRoot,
        IProgress<string>? progress, CancellationToken token, HttpClient? client = null) =>
        Task.Run(() => ResolveAsync(EndfieldVfsArchive.Open(game), selected, resourceRoot, progress, token, client), token);

    internal static async Task<string> ResolveAsync(EndfieldVfsArchive archive, string selected, string resourceRoot,
        IProgress<string>? progress, CancellationToken token, HttpClient? client = null)
    {
        var workspace = EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive), resourceRoot);
        try { return await ResolveCoreAsync(archive, selected, resourceRoot, workspace, progress, token, client).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var advice = error.Data[nameof(RecoveryAdvice)] as RecoveryAdvice ?? RecoveryHelp.Index(resourceRoot, workspace);
            throw ActivityLog.WithRecovery(error, advice);
        }
    }

    private static async Task<string> ResolveCoreAsync(EndfieldVfsArchive archive, string selected, string resourceRoot, string defaultWorkspace,
        IProgress<string>? progress, CancellationToken token, HttpClient? client)
    {
        var explicitPackage = !string.IsNullOrWhiteSpace(selected) && selected.EndsWith(".effindex.gz", StringComparison.OrdinalIgnoreCase);
        if (explicitPackage && !File.Exists(selected)) throw new FileNotFoundException("The selected portable index package is missing.", selected);
        var existing = explicitPackage ? null : EndfieldSharedIndexes.FindCurrent(archive, token, selected, defaultWorkspace);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        if (string.IsNullOrWhiteSpace(resourceRoot))
            throw new IOException("Choose a resource folder in Settings to store the game index.");
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

        progress?.Report("Finding a public index for this game's exact Bundle contents...");
        var published = await EndfieldIndexUpdates.Find(EndfieldIndexUpdates.DefaultSource, content, token, client!).ConfigureAwait(false);
        if (published is null || !string.Equals(published.Release.GameContent, content, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"EFF has not published an asset index for this game content ({content}).");

        var destination = EndfieldIndexUpdates.CachedPackage(published, resourceRoot);
        try
        {
            await EndfieldIndexUpdates.Download(published, destination, progress!, token, client!).ConfigureAwait(false);
            return Import(archive, workspace, destination, content, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            throw ActivityLog.WithRecovery(error, RecoveryHelp.Index(resourceRoot, workspace, published.Url));
        }
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
}
