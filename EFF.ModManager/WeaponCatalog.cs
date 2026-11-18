using AnimeStudio;
using AnimeStudio.GUI;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EffModManager;

internal sealed record WeaponChoice(string Id, string NameZh, string NameEn, string Prefab, string Bundle)
{
    internal string Type => Id.Split('_').ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
    internal string TypeLabel(bool english) => Type switch
    {
        "sword" => english ? "Sword" : "单手剑 · sword",
        "claym" => english ? "Greatsword" : "双手剑 · claym",
        "lance" => english ? "Polearm" : "长柄武器 · lance",
        "pistol" => english ? "Handcannon" : "手铳 · pistol",
        "funnel" => english ? "Arts Unit" : "施术单元 · funnel",
        _ => Type
    };

    internal static IEnumerable<WeaponChoice> Ordered(IEnumerable<WeaponChoice> choices, bool english)
        => choices.OrderBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Label(english), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Prefab, StringComparer.OrdinalIgnoreCase);

    internal string Label(bool english)
    {
        var name = english ? NameEn : NameZh;
        if (string.IsNullOrWhiteSpace(name)) name = Id;
        var variant = Path.GetFileNameWithoutExtension(Prefab);
        var suffix = variant.StartsWith(Id, StringComparison.OrdinalIgnoreCase) ? variant[Id.Length..] : "";
        if (suffix == "_refined") suffix = english ? " · Refined" : " · 精炼";
        else if (suffix.Length > 0) suffix = " · " + suffix.TrimStart('_');
        return $"{name}{suffix}  ({Id})";
    }
}

internal sealed record WeaponNode(string Position, string Rotation, string Scale);
internal sealed record WeaponMeshTarget(string Path, string Asset, string Bundle, string Renderer, int Lod, string Role,
    IReadOnlyList<WeaponNode>? Nodes = null);
internal sealed record WeaponTarget(WeaponChoice Choice, IReadOnlyList<WeaponMeshTarget> Meshes);
internal sealed class WeaponListing(IReadOnlyList<WeaponChoice> choices, Func<WeaponChoice, CancellationToken, WeaponTarget> resolve)
{
    internal IReadOnlyList<WeaponChoice> Choices { get; } = choices;
    internal WeaponTarget Resolve(WeaponChoice choice, CancellationToken token) => resolve(choice, token);
}

// Index-backed choices and source-table names are separate from author Mod names.
// Load once off the UI thread; only the selected Prefab is parsed when applying.
internal sealed class WeaponCatalog
{
    internal static WeaponListing Listing(string game, string resources, CancellationToken token, bool refresh = false)
    {
        WeaponCatalog? catalog = null;
        var saved = GameCatalogCache.Load(game, resources, "weapons", SharedResources.IndexWorkspace, () =>
        {
            catalog = Load(game, resources, token);
            return new CatalogData<WeaponChoice[]>(catalog.Choices.ToArray(), catalog._indexPath);
        }, token, refresh);
        return new(saved.Value, (choice, cancellation) =>
        {
            catalog ??= Load(game, resources, cancellation);
            return catalog.Resolve(choice, cancellation);
        });
    }

    private readonly EndfieldVfsArchive _archive;
    private readonly VirtualAssetRecord[] _meshes;
    private readonly IReadOnlyDictionary<string, string> _cabOwners;
    private readonly string _indexPath;
    private readonly IReadOnlyDictionary<string, EndfieldBundleIndexSpan> _spans;
    private readonly Dictionary<string, VirtualAssetRecord[]> _borrowedMeshes = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<WeaponChoice> Choices { get; }
    private WeaponCatalog(EndfieldVfsArchive archive, VirtualAssetRecord[] meshes,
        IReadOnlyDictionary<string, string> owners, IReadOnlyList<WeaponChoice> choices, string indexPath,
        IReadOnlyDictionary<string, EndfieldBundleIndexSpan> spans)
        => (_archive, _meshes, _cabOwners, Choices, _indexPath, _spans) = (archive, meshes, owners, choices, indexPath, spans);

    internal static WeaponCatalog Load(string game, string resourceRoot, CancellationToken token)
    {
        var archive = EndfieldVfsArchive.Open(game);
        var workspace = EndfieldSharedIndexes.FindCurrent(archive, token, resourceRoot);
        if (string.IsNullOrEmpty(workspace)) throw ActivityLog.WithRecovery(
            new FileNotFoundException(LanguageService.Instance["weapon.indexMissing"]),
            RecoveryHelp.Index(resourceRoot, EndfieldSharedIndexes.DefaultWorkspace(game, resourceRoot)));
        token.ThrowIfCancellationRequested();
        var indexPath = EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);
        var index = EndfieldIndexStore.Load(indexPath, recordFilter: r =>
            r.Type == "Mesh" && r.Container.Contains("/wpn/", StringComparison.OrdinalIgnoreCase) ||
            (r.Type is "GameObject" or "Mesh") && r.Container.Contains("/prefabs/weapons/", StringComparison.OrdinalIgnoreCase));
        token.ThrowIfCancellationRequested();
        var records = index.Assets.Records.ToArray();
        var meshes = records.Where(r => r.Type == "Mesh").ToArray();
        var sources = records.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(s => index.Dependencies.ResolveBundleClosure(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owners = sources
            .SelectMany(s => index.Dependencies.GetCabNames(s).Select(c => (Cab: c, Source: s)))
            .ToDictionary(p => p.Cab, p => p.Source, StringComparer.OrdinalIgnoreCase);
        var names = ReadNames(archive);
        var choices = new List<WeaponChoice>();
        foreach (var group in records.Where(r => r.Type == "GameObject").GroupBy(r => r.Container, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileNameWithoutExtension(group.Key);
            var id = names.Keys.Where(id => file.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                file.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(id => id.Length).FirstOrDefault();
            // Do not invent localized names for internal/test Prefabs absent from WeaponBasicTable.
            if (id is null) continue;
            var roots = group.Where(r => r.Name.Equals(file, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (roots.Length != 1) continue;
            var name = names[id];
            choices.Add(new(id, name.Zh, name.En, group.Key, roots[0].Source));
        }
        if (choices.Count == 0) throw new InvalidDataException("No indexed weapon Prefabs match WeaponBasicTable.");
        var cache = EndfieldBundleIndexCache.TryLoad(indexPath);
        if (cache is null || cache.Fingerprint != archive.Fingerprint) throw new InvalidDataException("Weapon index changed during loading.");
        return new(archive, meshes, owners, choices, indexPath,
            cache.Spans.Where(p => sources.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
    }

    internal static Dictionary<string, (string Zh, string En)> ReadNames(EndfieldVfsArchive archive)
    {
        var weapons = new GameDataTable(archive.ReadPayload("TableCfg/WeaponBasicTable.bytes"));
        var items = new GameDataTable(archive.ReadPayload("TableCfg/ItemTable.bytes"));
        var ids = items.Entries().Where(e => ((string)e.Key).StartsWith("wpn_", StringComparison.Ordinal))
            .ToDictionary(e => (string)e.Key, e => items.LocalizedId(e.Value, "name"), StringComparer.OrdinalIgnoreCase);
        return GameLocalization.ReadNames(archive, weapons.Entries().ToDictionary(e => (string)e.Key,
            e => ids.GetValueOrDefault((string)e.Key), StringComparer.OrdinalIgnoreCase));
    }

    internal void VerifyCurrent() => _archive.VerifyCurrent();

    internal WeaponTarget Resolve(WeaponChoice choice, CancellationToken token)
    {
        VerifyCurrent(); token.ThrowIfCancellationRequested();
        var manager = new AssetsManager { Game = GameManager.GetGameByType(GameType.ArknightsEndfield),
            Silent = true, ResolveDependencies = false };
        try
        {
            using var stream = new MemoryStream(_archive.ReadPayload(choice.Bundle), false);
            manager.LoadStream(choice.Bundle, stream);
            var rootName = Path.GetFileNameWithoutExtension(choice.Prefab);
            var roots = manager.assetsFileList.SelectMany(f => f.ObjectsDic.Values).OfType<GameObject>()
                .Where(g => g.m_Name.Equals(rootName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (roots.Length != 1 || roots[0].m_Transform is null) throw new InvalidDataException("Weapon Prefab root was not resolved: " + choice.Prefab);
            var targets = new List<WeaponMeshTarget>();
            void Visit(Transform transform, string path, IReadOnlyList<WeaponNode> nodes, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 128) throw new InvalidDataException("Weapon hierarchy is too deep.");
                if (!transform.m_GameObject.TryGet(out var go)) throw new InvalidDataException("Unresolved weapon GameObject.");
                var pointer = go.m_SkinnedMeshRenderer?.m_Mesh ?? go.m_MeshFilter?.m_Mesh;
                var owner = (AnimeStudio.Object?)go.m_SkinnedMeshRenderer ?? go.m_MeshFilter;
                if (pointer is { m_PathID: not 0 } && owner is not null)
                {
                    var source = choice.Bundle;
                    if (pointer.m_FileID > 0)
                    {
                        if (pointer.m_FileID > owner.assetsFile.m_Externals.Count) throw new InvalidDataException("Invalid weapon Mesh reference.");
                        var cab = owner.assetsFile.m_Externals[pointer.m_FileID - 1].fileName;
                        if (!_cabOwners.TryGetValue(cab, out source)) throw new InvalidDataException("Weapon Mesh CAB is absent from the index: " + cab);
                    }
                    var meshRecords = _meshes.Where(m => m.Source.Equals(source, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (!meshRecords.Any(m => m.PathId == pointer.m_PathID))
                    {
                        if (!_borrowedMeshes.TryGetValue(source, out meshRecords))
                        {
                            if (!_spans.TryGetValue(source, out var span)) throw new InvalidDataException("Weapon Mesh source is outside the Prefab dependency closure: " + source);
                            meshRecords = EndfieldIndexStore.ReadBundleAssets(_indexPath, span, token).Where(r => r.Type == "Mesh").ToArray();
                            _borrowedMeshes.Add(source, meshRecords);
                        }
                    }
                    var matches = meshRecords.Where(m => m.PathId == pointer.m_PathID)
                        .DistinctBy(m => (m.Container, m.Name)).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException($"Weapon Mesh identity is missing or ambiguous: {path} ({source}, {pointer.m_PathID}, {matches.Length} matches)");
                    var mesh = matches[0];
                    var lod = Regex.Match(mesh.Name, @"_lod(\d+)$", RegexOptions.IgnoreCase);
                    // Shared resources (including the pipeline Quad) are scoped
                    // through this weapon's native container/preload identity.
                    // Never emit a global replacement of a shared game Mesh.
                    var logicalTarget = WeaponId(mesh.Container) == choice.Id ? mesh.Container : choice.Prefab;
                    targets.Add(new(logicalTarget, mesh.Name, mesh.Source, path,
                        lod.Success ? int.Parse(lod.Groups[1].Value) : 0, Role(mesh.Name), nodes));
                }
                foreach (var child in transform.m_Children)
                {
                    if (!child.TryGet(out var next) || !next.m_GameObject.TryGet(out var childObject)) throw new InvalidDataException("Unresolved weapon Transform.");
                    string V(params float[] values) => string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
                    var node = new WeaponNode(V(next.m_LocalPosition.X, next.m_LocalPosition.Y, next.m_LocalPosition.Z),
                        V(next.m_LocalRotation.X, next.m_LocalRotation.Y, next.m_LocalRotation.Z, next.m_LocalRotation.W),
                        V(next.m_LocalScale.X, next.m_LocalScale.Y, next.m_LocalScale.Z));
                    Visit(next, path.Length == 0 ? childObject.m_Name : path + "/" + childObject.m_Name, nodes.Append(node).ToArray(), depth + 1);
                }
            }
            Visit(roots[0].m_Transform, "", [], 0);
            if (targets.Count == 0) throw new InvalidDataException("Weapon Prefab has no indexed Mesh renderers.");
            // A model logical path may also own unused auxiliary Meshes/LODs.
            // Include them in replacement coverage even when this Prefab does not
            // instantiate their renderer. Such records cannot be effect mounts.
            var modelPaths = targets.Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var mesh in _meshes.Where(m => modelPaths.Contains(m.Container)))
            {
                if (targets.Any(t => t.Path.Equals(mesh.Container, StringComparison.OrdinalIgnoreCase) &&
                    t.Asset.Equals(mesh.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var lod = Regex.Match(mesh.Name, @"_lod(\d+)$", RegexOptions.IgnoreCase);
                targets.Add(new(mesh.Container, mesh.Name, mesh.Source, "", lod.Success ? int.Parse(lod.Groups[1].Value) : 0, Role(mesh.Name)));
            }
            return new(choice, targets);
        }
        finally { manager.Clear(); }
    }

    internal static string WeaponId(string path)
    {
        var match = Regex.Match(path.Replace('\\', '/'), @"(?:^|/)(wpn_[a-z]+_\d+(?:_ink)?)(?:_|/|\.)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "";
    }
    internal static string Role(string asset) => Regex.Replace(
        Regex.Replace(asset, @"^S_wpn_[a-z]+_\d+(?:_ink)?", "", RegexOptions.IgnoreCase), @"_lod\d+$", "", RegexOptions.IgnoreCase).ToLowerInvariant();
}
