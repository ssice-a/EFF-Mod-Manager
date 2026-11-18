using AnimeStudio.GUI;
using System.Text.RegularExpressions;

namespace EffModManager;

internal sealed record CharacterTarget(string Section, string Path, string Asset);
internal sealed record CharacterBinding(string Identity, IReadOnlyList<CharacterTarget> Targets);

// This is an informational view of actual INI relationships. It has no retarget
// writer: character skeletons, physics and animation bindings remain authored.
internal sealed class CharacterTargets
{
    private readonly IReadOnlyDictionary<string, (string Zh, string En)> _names;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    internal CharacterTargets(IReadOnlyDictionary<string, (string Zh, string En)> names)
    {
        _names = names;
        foreach (var id in names.Keys)
        {
            var code = Regex.Replace(id, @"^chr_\d+_", "", RegexOptions.IgnoreCase);
            // Keep ambiguous aliases unresolved rather than choosing a character.
            if (!_aliases.TryAdd(code, id)) _aliases[code] = code;
        }
    }

    internal static CharacterTargets Load(string game, CancellationToken token)
    {
        var cached = GameCatalogCache.Load(game, SharedResources.ResourceRoot, "characters", "", () =>
            new CatalogData<Dictionary<string, string[]>>(ReadNames(game, token).ToDictionary(p => p.Key,
                p => new[] { p.Value.Zh, p.Value.En }, StringComparer.OrdinalIgnoreCase)), token);
        return new(cached.Value.ToDictionary(p => p.Key, p => (p.Value[0], p.Value[1]), StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, (string Zh, string En)> ReadNames(string game, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var archive = EndfieldVfsArchive.Open(game);
        var table = new GameDataTable(archive.ReadPayload("TableCfg/CharacterTable.bytes"));
        var ids = table.Entries().ToDictionary(e => (string)e.Key, e => table.LocalizedId(e.Value, "name"),
            StringComparer.OrdinalIgnoreCase);
        return GameLocalization.ReadNames(archive, ids, token);
    }

    internal string Label(string identity, bool english)
    {
        var id = _aliases.GetValueOrDefault(identity, identity);
        var names = _names.GetValueOrDefault(id);
        var name = english ? names.En : names.Zh;
        return string.IsNullOrWhiteSpace(name) ? id : $"{name}  ({id})";
    }

    internal IReadOnlyList<CharacterBinding> Resolve(IReadOnlyList<CharacterBinding> bindings)
        => bindings.GroupBy(b => _aliases.GetValueOrDefault(b.Identity, b.Identity), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CharacterBinding(g.Key, g.SelectMany(b => b.Targets).ToArray())).ToArray();

    internal static IReadOnlyList<CharacterBinding> Read(string iniPath)
    {
        var ini = ModService.ParseIni(File.ReadAllLines(iniPath));
        if (!ini.TryGetValue("Mod", out var mod) || mod.GetValueOrDefault("format") != "3") return [];
        var targets = new List<(string Identity, CharacterTarget Target)>();
        foreach (var section in ini.Where(s => s.Key.StartsWith("Replace", StringComparison.OrdinalIgnoreCase) ||
            s.Key.StartsWith("AttachEffect", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var field in section.Value.Where(p => p.Key.Equals("target.path", StringComparison.OrdinalIgnoreCase) ||
                p.Key.Equals("target.prefab", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(p.Key, @"^lod\.\d+\.path$", RegexOptions.IgnoreCase)))
            {
                var path = field.Value.Replace('\\', '/');
                var id = Regex.Match(path, @"(?:^|/)(chr_\d+_[a-z0-9]+)(?:_|/|\.)", RegexOptions.IgnoreCase);
                var actor = Regex.Match(path, @"/entity/actor/[^/]+/([^/]+)/", RegexOptions.IgnoreCase);
                var identity = id.Success ? id.Groups[1].Value : actor.Success ? actor.Groups[1].Value : "";
                if (identity.Length == 0) continue;
                var assetKey = field.Key.EndsWith(".path", StringComparison.OrdinalIgnoreCase) ? field.Key[..^4] + "asset" : "";
                targets.Add((identity.ToLowerInvariant(), new(section.Key, field.Value, section.Value.GetValueOrDefault(assetKey, ""))));
            }
        }
        return targets.Distinct().GroupBy(t => t.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CharacterBinding(g.Key, g.Select(t => t.Target).ToArray())).ToArray();
    }
}
