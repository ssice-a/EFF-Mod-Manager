#nullable enable annotations
#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeStudio.GUI;

internal sealed record EndfieldIndexRelease(string Format, string Version, string GameContent,
    string File, long Bytes, string Sha256, long IndexBytes, string IndexSha256);

internal sealed record IndexDownload(EndfieldIndexRelease Release, string Url);
internal sealed record RepairCatalog(string Format, string ToolVersion, string ToolUrl, IndexDownload[] Indexes);
internal sealed record BundleContentInfo(string Path, string FileDataMd5, long Length);

internal static class RepairCatalogLookup
{
    internal static IndexDownload FindExact(IEnumerable<RepairCatalog> catalogs, string gameContent)
    {
        foreach (var catalog in catalogs)
        {
            var match = catalog.Indexes.LastOrDefault(index =>
                string.Equals(index.Release.GameContent, gameContent, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    internal static IndexDownload FindLatest(IEnumerable<RepairCatalog> catalogs) => catalogs
        .SelectMany(catalog => catalog.Indexes)
        .OrderByDescending(index => index.Release.Version, StringComparer.Ordinal)
        .FirstOrDefault();

    internal static bool BundlePathsCompatible(IEnumerable<BundleContentInfo> published, IEnumerable<BundleContentInfo> installed)
    {
        var publishedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in published)
            if (!publishedPaths.Add(bundle.Path)) return false;

        var installedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in installed)
            if (!installedPaths.Add(bundle.Path)) return false;

        return publishedPaths.SetEquals(installedPaths);
    }
}
