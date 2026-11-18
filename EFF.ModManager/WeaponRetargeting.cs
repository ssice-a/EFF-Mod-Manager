using System.Text.RegularExpressions;

namespace EffModManager;

internal sealed record WeaponBinding(string Id, IReadOnlyList<string> Sections)
{
    public override string ToString() => Id;
}

internal static class WeaponRetargeting
{
    internal static IReadOnlyList<WeaponBinding> Bindings(string iniPath)
        => Bindings(ModService.ParseIni(File.ReadAllLines(iniPath)));

    private static IReadOnlyList<WeaponBinding> Bindings(Dictionary<string, Dictionary<string, string>> ini)
    {
        if (!ini.TryGetValue("Mod", out var mod) || mod.GetValueOrDefault("format") != "3") return [];
        return ini.Where(s => IsReplace(s.Key) || IsAttach(s.Key))
            .Select(s => (s.Key, Id: WeaponCatalog.WeaponId(s.Value.GetValueOrDefault(
                IsAttach(s.Key) ? "target.prefab" : "target.path", ""))))
            .Where(s => s.Id.Length > 0).GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new WeaponBinding(g.Key, g.Select(s => s.Key).ToArray())).ToArray();
    }

    private static bool IsReplace(string section) => section.StartsWith("Replace", StringComparison.OrdinalIgnoreCase)
        && !section.StartsWith("ReplaceEffect", StringComparison.OrdinalIgnoreCase);
    private static bool IsAttach(string section) => section.StartsWith("AttachEffect", StringComparison.OrdinalIgnoreCase);

    internal static byte[] Rewrite(byte[] original, WeaponBinding binding, WeaponTarget target)
    {
        // Read via the same encoding detection as the preserving writer.
        using var reader = new StreamReader(new MemoryStream(original), detectEncodingFromByteOrderMarks: true);
        var ini = ModService.ParseIni(reader.ReadToEnd().Split('\n'));
        var actual = Bindings(ini).SingleOrDefault(b => b.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase));
        if (actual is null || !actual.Sections.SequenceEqual(binding.Sections)) throw new IOException(LanguageService.Instance["weapon.changed"]);
        var changes = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<(string Path, string Asset)>();
        foreach (var name in actual.Sections)
        {
            var old = ini[name];
            var attach = IsAttach(name);
            // Part-mounted effects follow Replace; only explicit game targets are edited here.
            var role = WeaponCatalog.Role(old.GetValueOrDefault("target.asset", ""));
            var candidates = target.Meshes.Where(m => !attach || m.Renderer.Length > 0).ToArray();
            var family = candidates.Where(m => m.Role == role).OrderBy(m => m.Lod).ToArray();
            if (family.Length == 0 && role == "_01")
            {
                var primaryFamilies = candidates.Where(m => !m.Role.Contains("vfx", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(m => m.Role).ToArray();
                if (primaryFamilies.Length == 1) family = primaryFamilies[0].OrderBy(m => m.Lod).ToArray();
            }
            if (family.Length == 0)
            {
                // An absent auxiliary skip target should not keep hiding the old weapon.
                if (!attach && old.GetValueOrDefault("handling") == "skip")
                {
                    removed.Add(name);
                    continue;
                }
                throw new IOException(string.Format(LanguageService.Instance["weapon.noMesh"], name, target.Choice.Id));
            }
            // Multiple renderers with the same role/LOD cannot be mapped by a one-click choice.
            if (family.GroupBy(m => m.Lod).Any(g => g.Count() != 1))
                throw new IOException(string.Format(LanguageService.Instance["weapon.noMesh"], name, target.Choice.Id));
            var selectedLod = Regex.Match(old.GetValueOrDefault("target.asset", ""), @"_lod(\d+)$", RegexOptions.IgnoreCase);
            var primary = family.FirstOrDefault(m => selectedLod.Success && m.Lod == int.Parse(selectedLod.Groups[1].Value)) ?? family[0];
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            bool TargetKey(string key)
            {
                key = key.ToLowerInvariant();
                return key is "target.path" or "target.asset" or "target.bundle" ||
                    Regex.IsMatch(key, @"^lod\.\d+\.(path|asset|bundle|renderer|bones)$") ||
                    attach && (key is "target.prefab" or "renderer" or "lod_anchor" || key.StartsWith("lod_anchor."));
            }
            foreach (var key in old.Keys.Where(TargetKey)) values[key] = null;
            values["target.path"] = primary.Path;
            values["target.asset"] = primary.Asset;
            if (attach)
            {
                values["target.prefab"] = target.Choice.Prefab;
                values["renderer"] = primary.Renderer;
            }
            var allLods = !attach || old.GetValueOrDefault("lod_coverage", "all") == "all";
            var siblings = allLods ? family.Where(m => m != primary).ToArray() : [];
            var index = 1;
            foreach (var mesh in siblings)
            {
                var prefix = "lod." + index++ + ".";
                values[prefix + "path"] = mesh.Path;
                values[prefix + "asset"] = mesh.Asset;
                if (attach) values[prefix + "renderer"] = mesh.Renderer;
            }
            if (attach && string.IsNullOrEmpty(old.GetValueOrDefault("bone")))
            {
                var common = primary.Renderer.Split('/').SkipLast(1).ToArray();
                foreach (var mesh in siblings)
                {
                    var path = mesh.Renderer.Split('/').SkipLast(1).ToArray();
                    common = common.Take(common.Zip(path).TakeWhile(p => p.First == p.Second).Count()).ToArray();
                }
                values["lod_anchor"] = common.Length == 0 ? "." : string.Join("/", common);
                var nodes = (primary.Nodes ?? []).Skip(common.Length).ToList();
                // The author exporter appends a mesh-to-Transform coordinate bridge.
                // Keep this authored suffix while replacing the native hierarchy prefix.
                var last = old.Keys.Where(k => Regex.IsMatch(k, @"^lod_anchor\.\d+\.rotation$"))
                    .OrderBy(k => int.Parse(k.Split('.')[1])).LastOrDefault();
                if (last is not null && IsMeshBridge(old[last]))
                    nodes.Add(new("0,0,0", old[last], "1,1,1"));
                if (nodes.Count == 0) nodes.Add(new("0,0,0", "0,0,0,1", "1,1,1"));
                for (var i = 0; i < nodes.Count; i++)
                {
                    values[$"lod_anchor.{i}.position"] = nodes[i].Position;
                    values[$"lod_anchor.{i}.rotation"] = nodes[i].Rotation;
                    values[$"lod_anchor.{i}.scale"] = nodes[i].Scale;
                }
            }
            changes.Add(name, values);
            if (!attach) foreach (var mesh in family) covered.Add((mesh.Path.ToLowerInvariant(), mesh.Asset.ToLowerInvariant()));
        }
        if (actual.Sections.Any(IsReplace))
        {
            // An entire weapon is one replacement. Render the authored parts once
            // per LOD, and suppress every other original Mesh, including VFX.
            foreach (var family in target.Meshes.DistinctBy(m => (m.Path.ToLowerInvariant(), m.Asset.ToLowerInvariant()))
                .Where(m => !covered.Contains((m.Path.ToLowerInvariant(), m.Asset.ToLowerInvariant()))).GroupBy(m => m.Role))
            {
                var meshes = family.OrderBy(m => m.Lod).ToArray();
                var stem = "ReplaceWeapon_" + Regex.Replace(target.Choice.Id + family.Key, @"[^A-Za-z0-9_]", "_");
                var name = stem;
                for (var suffix = 2; ini.ContainsKey(name) || changes.ContainsKey(name); suffix++) name = stem + "_" + suffix;
                var fields = new Dictionary<string, string?> { ["target.path"] = meshes[0].Path,
                    ["target.asset"] = meshes[0].Asset, ["handling"] = "skip" };
                for (var i = 1; i < meshes.Length; i++)
                {
                    fields[$"lod.{i}.path"] = meshes[i].Path;
                    fields[$"lod.{i}.asset"] = meshes[i].Asset;
                }
                changes.Add(name, fields);
            }
        }
        return ModService.UpdateIni(original, changes, removed);
    }

    private static bool IsMeshBridge(string text)
    {
        var values = text.Split(',');
        return values.Length == 4 && values.Select(s => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Zip(new[] { Math.Sqrt(.5), 0, 0, Math.Sqrt(.5) }).All(p => Math.Abs(p.First - p.Second) < 1e-5);
    }

    internal static void Apply(ModEntry mod, WeaponBinding binding, WeaponTarget target, byte[] expected)
    {
        var updated = Rewrite(expected, binding, target);
        Format3Validation.ValidateBytes(mod.Folder, updated, CancellationToken.None);
        if (!File.ReadAllBytes(mod.IniPath).AsSpan().SequenceEqual(expected)) throw new IOException(LanguageService.Instance["weapon.changed"]);
        var backupRoot = Path.Combine(mod.Folder, ".effmm-backups");
        Directory.CreateDirectory(backupRoot);
        File.WriteAllBytes(Path.Combine(backupRoot, "weapon-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".ini"), expected);
        ModService.WriteBytesAtomic(mod.IniPath, updated);
    }
}
