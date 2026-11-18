using EffModManager;
using System.Text;

internal static class WeaponTests
{
    internal static void Run(string root)
    {
        VerifyLongKeyAlignment();
        var folder = Path.Combine(root, "weapon-retarget");
        Directory.CreateDirectory(folder);
        var iniPath = Path.Combine(folder, "mod.ini");
        const string source = """
            ; author comments survive
            [Mod]
            format=3
            id=StableAuthor
            controls=ini
            [MeshBlade]
            path=meshes/MyBlade.effres
            [MaterialBlade]
            source.path=assets/source/wpn_sword_0001/material.mat
            [PartBlade]
            mesh=MeshBlade
            [ReplaceBlade]
            target.path=assets/models/wpn_sword_0001/model.fbx
            target.asset=S_wpn_sword_0001_01_lod0
            TARGET.BUNDLE=old.ab
            parts=PartBlade
            future_field=keep
            [ReplaceOldVfx]
            target.path=assets/models/wpn_sword_0001/model.fbx
            target.asset=S_wpn_sword_0001_vfx_01_lod0
            handling=skip
            [AttachEffectBlade]
            target.prefab=assets/prefabs/wpns/wpn_sword_0001_refined.prefab
            renderer=Old/lod0/Blade
            target.path=assets/models/wpn_sword_0001/model.fbx
            target.asset=S_wpn_sword_0001_01_lod0
            lod.1.renderer=Old/lod1/Blade
            lod.1.path=assets/models/wpn_sword_0001/model.fbx
            lod.1.asset=S_wpn_sword_0001_01_lod1
            lod_coverage=all
            lod_anchor=Old
            lod_anchor.0.position=1,2,3
            lod_anchor.0.rotation=0,0,0,1
            lod_anchor.0.scale=1,1,1
            lod_anchor.1.position=0,0,0
            lod_anchor.1.rotation=0.7071067811865476,0,0,0.7071067811865476
            lod_anchor.1.scale=1,1,1
            effect=ResourceSparks
            position=1,2,3
            [AttachEffectPart]
            part=PartBlade
            target.prefab=assets/prefabs/wpns/wpn_sword_0001_refined.prefab
            target.path=assets/models/wpn_sword_0001/model.fbx
            target.asset=S_wpn_sword_0001_01_lod0
            renderer=Old/lod0/Blade
            [ReplaceEffectUnrelated]
            target.prefab=assets/prefabs/wpns/wpn_sword_0001_refined.prefab
            effect=ResourceOther
            [ReplaceAnotherWeapon]
            target.path=assets/models/wpn_lance_0004/model.fbx
            target.asset=S_wpn_lance_0004_01_lod0
            parts=PartOther
            [KeyToggle]
            key=NUMPAD1
            $mode = ($mode + 1) % 2
            [FutureSection]
            path=do_not_touch
            """;
        File.WriteAllText(iniPath, source.Replace("\n", "\r\n"), new UnicodeEncoding(false, true));
        var original = File.ReadAllBytes(iniPath);
        var binding = WeaponRetargeting.Bindings(iniPath).Single(b => b.Id == "wpn_sword_0001");
        Require(binding.Sections.Count == 4, "Weapon relationships were not grouped or ReplaceEffect was mistaken for an attachment.");
        var target = Target();
        var rewritten = WeaponRetargeting.Rewrite(original, binding, target);
        Require(rewritten[0] == 0xff && rewritten[1] == 0xfe, "Retarget lost the author INI encoding.");
        var text = Encoding.Unicode.GetString(rewritten.AsSpan(2));
        var parsed = ModService.ParseIni(text.Split('\n'));
        var main = parsed["ReplaceBlade"];
        Require(main["parts"] == "PartBlade" && !main.ContainsKey("target.bundle") && main["future_field"] == "keep", "Author parts/unknown fields were changed or stale Bundle hint survived.");
        Require(main["lod.4.asset"].EndsWith("lod5"), "Complete native LOD coverage was not written, including indexed non-renderer LODs.");
        var effects = parsed["AttachEffectBlade"];
        Require(effects["target.prefab"] == target.Choice.Prefab && effects["renderer"] == "Mesh_all/lod0/Blade" &&
            effects["lod.3.renderer"] == "Mesh_all/lod3/Blade" && !effects.ContainsKey("lod.4.renderer"), "Effect mounts did not follow all real renderer LODs.");
        Require(effects["lod_anchor"] == "Mesh_all" && effects["lod_anchor.0.position"] == "0,0,0" &&
            effects["lod_anchor.2.rotation"].StartsWith("0.707"), "Native mount hierarchy or author coordinate bridge was lost.");
        Require(parsed["AttachEffectPart"]["part"] == "PartBlade" && effects["position"] == "1,2,3", "Part links or authored mount offsets changed.");
        Require(parsed["ReplaceAnotherWeapon"]["target.asset"] == "S_wpn_lance_0004_01_lod0" &&
            parsed["ReplaceEffectUnrelated"]["effect"] == "ResourceOther" &&
            parsed["MaterialBlade"]["source.path"].Contains("wpn_sword_0001") && text.Contains("$mode = ($mode + 1) % 2") &&
            text.Contains("; author comments survive\r\n") && parsed["FutureSection"]["path"] == "do_not_touch", "Retarget touched other weapon, resources, replacement effects, control logic or comments.");
        var coverage = parsed.Where(s => s.Key.StartsWith("Replace") && !s.Key.StartsWith("ReplaceEffect"))
            .SelectMany(s => s.Value.Where(k => k.Key == "target.asset" || k.Key.StartsWith("lod.") && k.Key.EndsWith(".asset")).Select(k => k.Value)).ToHashSet();
        Require(target.Meshes.All(m => coverage.Contains(m.Asset)), "An original Mesh, VFX or LOD was left uncovered.");
        var effectOnlyPath = Path.Combine(folder, "effect-only.ini");
        File.WriteAllText(effectOnlyPath, "[Mod]\nformat=3\n[AttachEffectOnly]\ntarget.prefab=assets/weapons/wpn_sword_0001.prefab\nrenderer=Mesh_all/lod0/Blade\ntarget.path=assets/models/wpn_sword_0001/model.fbx\ntarget.asset=S_wpn_sword_0001_01_lod0\n");
        var effectOnly = ModService.ParseIni(Encoding.UTF8.GetString(WeaponRetargeting.Rewrite(File.ReadAllBytes(effectOnlyPath),
            WeaponRetargeting.Bindings(effectOnlyPath).Single(), target)).Split('\n'));
        Require(!effectOnly.Keys.Any(k => k.StartsWith("Replace")) && effectOnly["AttachEffectOnly"]["target.prefab"] == target.Choice.Prefab,
            "Retargeting a mount-only Mod added Mesh replacement/skip rules.");
        Require(parsed.Values.Any(s => s.GetValueOrDefault("target.asset") == "S_wpn_sword_0002_extra_lod0" && s.GetValueOrDefault("handling") == "skip"), "Original auxiliary mesh was left visible.");
        var noVfx = target with { Meshes = target.Meshes.Where(m => !m.Role.Contains("vfx")).ToArray() };
        var withoutVfx = ModService.ParseIni(Encoding.Unicode.GetString(WeaponRetargeting.Rewrite(original, binding, noVfx).AsSpan(2)).Split('\n'));
        Require(!withoutVfx.ContainsKey("ReplaceOldVfx"), "A missing old VFX left an invalid empty rule or hid the old weapon.");
        // Publication goes through the actual shipped declaration validator.
        Directory.CreateDirectory(Path.Combine(folder, "meshes"));
        File.WriteAllText(Path.Combine(folder, "meshes", "MyBlade.effres"), "native payload validated on demand");
        var publishSource = "[Mod]\nformat=3\nid=StableAuthor\ncontrols=ini\n[MeshBlade]\npath=meshes/MyBlade.effres\n[PartBlade]\nmesh=MeshBlade\nvisible=1\n[ReplaceBlade]\ntarget.path=assets/models/wpn_sword_0001/model.fbx\ntarget.asset=S_wpn_sword_0001_01_lod0\nparts=PartBlade\n";
        File.WriteAllText(iniPath, publishSource);
        original = File.ReadAllBytes(iniPath);
        binding = WeaponRetargeting.Bindings(iniPath).Single();
        rewritten = WeaponRetargeting.Rewrite(original, binding, target);
        var mod = ModService.ScanTree(folder).Single();
        File.Delete(Path.Combine(folder, "meshes", "MyBlade.effres"));
        try { WeaponRetargeting.Apply(mod, binding, target, original); throw new Exception("Invalid candidate was published."); }
        catch (InvalidDataException)
        {
            Require(File.ReadAllBytes(iniPath).SequenceEqual(original) && !Directory.Exists(Path.Combine(folder, ".effmm-backups")),
                "Failed declaration validation changed the descriptor or created a misleading backup.");
        }
        File.WriteAllText(Path.Combine(folder, "meshes", "MyBlade.effres"), "native payload validated on demand");
        WeaponRetargeting.Apply(mod, binding, target, original);
        Require(File.ReadAllBytes(iniPath).SequenceEqual(rewritten) && Directory.GetFiles(Path.Combine(folder, ".effmm-backups")).Length == 1, "Target publication or INI backup failed.");
        var externallyEdited = File.ReadAllBytes(iniPath);
        try { WeaponRetargeting.Apply(mod, binding, target, original); throw new Exception("External edit was overwritten."); }
        catch (IOException) { Require(File.ReadAllBytes(iniPath).SequenceEqual(externallyEdited), "External edit changed on rejection."); }
        ModService.RenameDisplayName(mod, "自定义刀 / Author:Blade");
        var renamed = ModService.ScanTree(folder).Single();
        Require(renamed.Name == "自定义刀 / Author:Blade" && renamed.Folder == folder && renamed.Id == "StableAuthor", "Display rename changed folder or authored identity, or failed to persist a previously absent name.");
        Require(WeaponRetargeting.Bindings(iniPath).Any(b => b.Id == "wpn_sword_0002"), "Display rename changed target bindings.");
        VerifyIndependentRetargets(root, mod, target);
        Require(target.Choice.Label(false).Contains("测试刀") && !target.Choice.Label(false).Contains("Test Blade") &&
            target.Choice.Label(true).Contains("Test Blade") && !target.Choice.Label(true).Contains("测试刀"), "Weapon labels mix language settings.");
        LanguageService.Instance.IsEnglish = true;
        Require(LanguageService.Instance["weapon.title"] == "Target weapon", "English UI label is missing.");
        LanguageService.Instance.IsEnglish = false;
        Require(LanguageService.Instance["weapon.title"] == "目标武器", "Chinese UI label is missing.");
        Console.WriteLine("Weapon retarget: full Mesh/VFX/LOD coverage, attachment bases, isolated author resources, names, language and atomic publication passed.");
    }

    private static WeaponTarget Target()
    {
        var choice = new WeaponChoice("wpn_sword_0002", "测试刀", "Test Blade", "assets/prefabs/weapons/wpn_sword_0002_refined.prefab", "new.ab");
        var nodes = new[] { new WeaponNode("0,0,0", "0,0,0,1", "1,1,1"), new WeaponNode("0,0,0", "0,0,0,1", "1,1,1"),
            new WeaponNode("0,0,0", "0,0,0,1", "1,1,1") };
        var meshes = new List<WeaponMeshTarget>();
        foreach (var role in new[] { "_01", "_vfx_01" })
            for (var lod = 0; lod < 4; lod++) meshes.Add(new("assets/models/wpn_sword_0002/model.fbx",
                $"S_wpn_sword_0002{role}_lod{lod}", "new.ab", $"Mesh_all/lod{lod}/Blade", lod, role, nodes));
        meshes.Add(new("assets/models/wpn_sword_0002/model.fbx", "S_wpn_sword_0002_01_lod5", "new.ab", "", 5, "_01"));
        meshes.Add(new("assets/models/wpn_sword_0002/model.fbx", "S_wpn_sword_0002_extra_lod0", "new.ab", "Mesh_all/Extra", 0, "_extra"));
        return new(choice, meshes);
    }

    private static void VerifyIndependentRetargets(string root, ModEntry source, WeaponTarget firstTarget)
    {
        var game = Path.Combine(root, "copy-game");
        var sourceBytes = File.ReadAllBytes(source.IniPath);
        var copy = ModService.DuplicateAsync(source, game, CancellationToken.None).GetAwaiter().GetResult();
        Require(copy.Id == source.Id && copy.Folder != source.Folder && copy.Name != source.Name && copy.IsEnabled,
            "Creating a weapon copy changed the author identity or did not create an independent visible installation.");
        var copiedMesh = File.ReadAllBytes(Path.Combine(copy.Folder, "meshes", "MyBlade.effres"));
        var choice = firstTarget.Choice with { Id = "wpn_sword_0003", Prefab = firstTarget.Choice.Prefab.Replace("0002", "0003") };
        var next = new WeaponTarget(choice, firstTarget.Meshes.Select(m => m with
        {
            Path = m.Path.Replace("0002", "0003"), Asset = m.Asset.Replace("0002", "0003")
        }).ToArray());
        var original = File.ReadAllBytes(copy.IniPath);
        var binding = WeaponRetargeting.Bindings(copy.IniPath).Single(b => b.Id == firstTarget.Choice.Id);
        WeaponRetargeting.Apply(copy, binding, next, original);
        Require(File.ReadAllBytes(source.IniPath).SequenceEqual(sourceBytes) &&
            WeaponRetargeting.Bindings(source.IniPath).Any(b => b.Id == "wpn_sword_0002") &&
            WeaponRetargeting.Bindings(copy.IniPath).Any(b => b.Id == "wpn_sword_0003"),
            "Retargeting a same-author copy also changed the original weapon target.");
        Require(File.ReadAllBytes(Path.Combine(copy.Folder, "meshes", "MyBlade.effres")).SequenceEqual(copiedMesh) &&
            Directory.GetFiles(Path.Combine(copy.Folder, ".effmm-backups")).Length == 1,
            "Retargeting mixed authored resources or copied another installation's backup history.");
        ModService.SetEnabled(copy, false);
        var disabled = ModService.ScanTree(copy.Folder).Single();
        var disabledCopy = ModService.DuplicateAsync(disabled, game, CancellationToken.None).GetAwaiter().GetResult();
        Require(!disabledCopy.IsEnabled && File.ReadAllBytes(source.IniPath).SequenceEqual(sourceBytes),
            "Copying/toggling a disabled installation changed another copy or silently enabled it.");
        Console.WriteLine("PASS: independent same-author weapon copies keep different targets, resources, backups and enable states.");
    }

    private static void VerifyLongKeyAlignment()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(12); writer.Write(16); writer.Write(64); writer.Write(0); // no structs
        writer.Write((byte)10); writer.Write(Encoding.UTF8.GetBytes("Names\0")); writer.Write((byte)3); writer.Write((byte)7);
        stream.Position = 40; writer.Write(Encoding.UTF8.GetBytes("Localized\0"));
        stream.Position = 64; writer.Write(1); writer.Write(76); writer.Write(1); // bucket pointer before 64-bit alignment
        stream.Position = 80; writer.Write(1234567890123L); writer.Write(40);
        var table = new GameDataTable(stream.ToArray()); var entry = table.Entries().Single();
        Require((long)entry.Key == 1234567890123L && table.Text(entry.Value) == "Localized", "TableCfg 64-bit key alignment or string pointer was misread.");
        var broken = stream.ToArray(); BitConverter.GetBytes(int.MaxValue).CopyTo(broken, 88);
        try { _ = new GameDataTable(broken).Text(entry.Value); throw new Exception("Invalid table pointer accepted."); }
        catch (InvalidDataException) { }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
