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

internal static class RepairCatalogLookup
{
    internal static IndexDownload Find(IEnumerable<RepairCatalog> catalogs, string gameContent)
    {
        foreach (var catalog in catalogs)
        {
            var match = catalog.Indexes.LastOrDefault(index =>
                string.Equals(index.Release.GameContent, gameContent, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }
}
