using EffModManager;

internal static class CharacterTargetTests
{
    internal static void Run(string root)
    {
        var file = Path.Combine(root, "character-targets.ini");
        File.WriteAllText(file, """
            [Mod]
            format=3
            name=Author name is not a character
            [MaterialBorrowed]
            source.path=assets/beyond/arts/entity/actor/girl/wulfa/materials/borrowed.mat
            [ReplaceBody]
            target.path=assets/beyond/arts/entity/actor/girl/chen/models/body.asset
            target.asset=S_actor_chen_body_lod0
            lod.1.path=assets/beyond/arts/entity/actor/girl/chen/models/body_lod1.asset
            lod.1.asset=S_actor_chen_body_lod1
            [AttachEffectHair]
            target.prefab=assets/beyond/dynamicassets/gameplay/prefabs/characters/chr_0005_chen.prefab
            [ReplaceSecondCharacter]
            target.path=assets/beyond/dynamicassets/gameplay/actors/postmodels/characters/chr_0030_zhuangfy_postmodel.asset
            target.asset=Body
            [ReplaceWeapon]
            target.path=assets/models/wpn_sword_0022/model.fbx
            [ResourceIgnored]
            path=assets/beyond/dynamicassets/gameplay/prefabs/characters/chr_0004_pelica.prefab
            """);
        var original = File.ReadAllBytes(file);
        var bindings = CharacterTargets.Read(file);
        var catalog = new CharacterTargets(new Dictionary<string, (string Zh, string En)>
        {
            ["chr_0005_chen"] = ("陈千语", "Chen Qianyu"),
            ["chr_0030_zhuangfy"] = ("庄方宜", "Zhuang Fangyi")
        });
        var resolved = catalog.Resolve(bindings);
        Require(resolved.Count == 2, "Character relations were duplicated by aliases, or borrowed source resources became targets.");
        var chen = resolved.Single(b => b.Identity == "chr_0005_chen");
        Require(chen.Targets.Count == 3 && chen.Targets.Any(t => t.Asset.EndsWith("lod1")) &&
            chen.Targets.Any(t => t.Path.EndsWith("chr_0005_chen.prefab")), "Mesh LODs or effect mount Prefab targets were omitted.");
        Require(catalog.Label(chen.Identity, false).StartsWith("陈千语") &&
            catalog.Label(chen.Identity, true).StartsWith("Chen Qianyu") &&
            catalog.Label("chen", false) == catalog.Label(chen.Identity, false), "Character names do not follow language/alias identity.");
        Require(catalog.Label("unlisted", false) == "unlisted" && File.ReadAllBytes(file).SequenceEqual(original),
            "Unknown character lost its identity, or viewing targets changed the INI.");
        File.WriteAllText(file, "[Mod]\nformat=3\n[ReplaceWeapon]\ntarget.path=assets/models/wpn_sword_0022/model.fbx\n[MaterialActor]\nsource.path=assets/beyond/arts/entity/actor/girl/chen/models/body.asset\n");
        Require(CharacterTargets.Read(file).Count == 0, "A weapon with a borrowed character material became a character Mod.");

        var choices = new[]
        {
            new WeaponChoice("wpn_sword_0001", "乙", "Beta", "sword-b.prefab", ""),
            new WeaponChoice("wpn_lance_0001", "丙", "Lance", "lance.prefab", ""),
            new WeaponChoice("wpn_sword_0002", "甲", "Alpha", "sword-a.prefab", ""),
            new WeaponChoice("wpn_claym_0001", "丁", "Greatsword", "claym.prefab", "")
        };
        var ordered = WeaponChoice.Ordered(choices, true).ToArray();
        Require(ordered.Select(c => c.Type).SequenceEqual(new[] { "claym", "lance", "sword", "sword" }) &&
            ordered[^2].NameEn == "Alpha", "Weapon choices are not contiguous by type and sorted by localized name within types.");
        Require(choices[0].TypeLabel(false).Contains("单手剑") && choices[0].TypeLabel(true) == "Sword",
            "Weapon type headers ignore the selected language.");
        Console.WriteLine("Character targets: read-only INI relationships, LODs, alias merging, language, source isolation and weapon type ordering passed.");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
