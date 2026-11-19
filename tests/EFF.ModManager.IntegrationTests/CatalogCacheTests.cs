using EffModManager;

internal static class CatalogCacheTests
{
    internal static async Task RunAsync(string root)
    {
        var game = Path.Combine(root, "catalog-game");
        var resources = Path.Combine(root, "catalog-resources");
        Directory.CreateDirectory(game);
        var blc = Path.Combine(game, "fixture.blc");
        var index = Path.Combine(root, "catalog-index", "endfield_assets.eidx");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(blc, "not a VFS; must not be parsed on a catalog hit");
        File.WriteAllText(index, "not an index; must not be parsed on a catalog hit");
        File.WriteAllText(index + ".bundles", "metadata fixture");
        var choices = new[] { new WeaponChoice("wpn_sword_0001", "Test CN", "Test EN", "assets/wpns/wpn_sword_0001.prefab", "test.ab") };
        var builds = 0;
        CatalogData<WeaponChoice[]> Build() { builds++; return new(choices, index); }
        GameCatalogCache.Load(game, resources, "weapons", SharedResources.IndexWorkspace, Build, CancellationToken.None);
        GameCatalogCache.Load(game, SharedResources.ResourceRoot, "characters", "", () =>
            new CatalogData<Dictionary<string, string[]>>(new() { ["chr_0001_test"] = ["Character CN", "Character EN"] }), CancellationToken.None);
        Require(builds == 1, "First lookup failed to build the presentation catalog.");
        using (var exclusiveIndex = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var exclusiveVfs = new FileStream(blc, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // New listing objects mimic new Manager sessions. Neither the index nor VFS is readable.
            for (var run = 0; run < 2; run++)
            {
                Require((await WeaponCatalog.ListingAsync(game, resources, null, CancellationToken.None)).Choices.Single() == choices[0],
                    "Restarted listing lost the bilingual weapon choices.");
                Require(CharacterTargets.Load(game, CancellationToken.None).Label("chr_0001_test", true).Contains("Character EN"),
                    "Character names re-read VFS instead of the persisted name list.");
            }
        }
        GameCatalogCache.Load(game, resources, "weapons", SharedResources.IndexWorkspace, Build, CancellationToken.None, refresh: true);
        Require(builds == 2, "Manual refresh failed to rebuild the catalog.");
        File.AppendAllText(index + ".bundles", "changed");
        GameCatalogCache.Load(game, resources, "weapons", SharedResources.IndexWorkspace, Build, CancellationToken.None);
        Require(builds == 3, "Changed index did not invalidate the catalog.");
        File.AppendAllText(blc, "changed");
        GameCatalogCache.Load(game, resources, "weapons", SharedResources.IndexWorkspace, Build, CancellationToken.None);
        Require(builds == 4, "Changed game metadata did not invalidate the catalog.");
        GameCatalogCache.Load(game, resources, "weapons", "different workspace", Build, CancellationToken.None);
        Require(builds == 5, "Switching index workspace reused an unrelated catalog.");
        Console.WriteLine("PASS: restarted weapon lists use the persisted bilingual catalog with index/VFS locked; manual refresh, index/game changes and workspace switches invalidate it.");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
