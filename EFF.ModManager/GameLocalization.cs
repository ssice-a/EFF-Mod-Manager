using AnimeStudio.GUI;

namespace EffModManager;

internal static class GameLocalization
{
    internal static Dictionary<string, (string Zh, string En)> ReadNames(EndfieldVfsArchive archive,
        IReadOnlyDictionary<string, long> ids, CancellationToken token = default)
    {
        var wanted = ids.Values.ToHashSet();
        Dictionary<long, string> Texts(string language)
        {
            token.ThrowIfCancellationRequested();
            var table = new GameDataTable(archive.ReadPayload($"TableCfg/I18nTextTable_{language}.bytes"));
            return table.Entries().Where(e => wanted.Contains((long)e.Key))
                .ToDictionary(e => (long)e.Key, e => table.Text(e.Value));
        }
        var zh = Texts("CN"); var en = Texts("EN");
        return ids.ToDictionary(p => p.Key, p => (zh.GetValueOrDefault(p.Value, ""), en.GetValueOrDefault(p.Value, "")),
            StringComparer.OrdinalIgnoreCase);
    }
}
