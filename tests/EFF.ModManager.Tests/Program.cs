using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AnimeStudio.GUI;
using EffModManager;
using EffRepair;

var fixture = Path.Combine(Path.GetTempPath(), "effmm-tests-" + Guid.NewGuid().ToString("N"));
var game = Path.Combine(fixture, "game");
var mods = ModService.ModsRoot(game);
var input = Path.Combine(fixture, "sample-pack.zip");
Directory.CreateDirectory(mods);
Directory.CreateDirectory(Path.GetDirectoryName(input)!);

try
{
    VerifyUnpackedWorkspaceLookup(fixture);
    VerifySharedResourceSettings(fixture);
    VerifyCompiledModCacheMigration(fixture);
    VerifyExternalDllVersionDetection(fixture);
    await VerifyHistoricalIndexLookupAsync();
    Directory.CreateDirectory(Path.Combine(game, "Endfield_Data"));
    File.WriteAllText(Path.Combine(game, "Endfield.exe"), "game");
    File.WriteAllText(Path.Combine(game, "GameAssembly.dll"), "assembly");
    File.Copy(typeof(GameDiscovery).Assembly.Location, Path.Combine(game, "plugin", "eff.dll"));
    if (!GameDiscovery.IsGame(game) || DllInstallService.IsManaged(game))
        throw new InvalidOperationException("A valid game with an externally installed EFF DLL was rejected or incorrectly marked as managed.");
    if (DllInstallService.InstalledVersion(game) is null)
        throw new InvalidOperationException("The external EFF DLL was not detected before Mod management.");

    if (args.Contains("--github-dll"))
    {
        await VerifyOfficialDllInstallAsync(fixture);
        return;
    }
    if (args.Contains("--github-repair"))
    {
        await VerifyOfficialRepairAssetAsync(fixture);
        return;
    }
    if (args.Contains("--github-updates"))
    {
        await VerifyOfficialUpdateChecksAsync(fixture);
        return;
    }
    if (args.Contains("--live-repair"))
    {
        await VerifyLiveRepairAsync(fixture);
        return;
    }

    CreateZip(input, ("Example/mod.ini", "[Mod]\nid=ExampleOne\nname=Example Mod\nauthor=Test Author\nversion=1.2\ndescription=A test package\n[KeyForward]\nkey=NUMPAD1\ntype=cycle\n"),
        ("Example/meshes/example.mesh", "payload"));
    await ModService.ImportAsync(input, game, CancellationToken.None);
    var mod = Single(ModService.Scan(game));
    Equal("Example Mod", mod.Name, "INI display name");
    Equal("Test Author", mod.Author, "INI author");
    Equal("1.2", mod.Version, "INI version");
    Equal("NUMPAD1  ·  KeyForward", mod.Hotkeys.Single(), "INI hotkey");
    if (!mod.IsEnabled) throw new InvalidOperationException("New Mod was not enabled by default.");

    ModService.SetEnabled(mod, false);
    var disabled = Single(ModService.Scan(game));
    if (disabled.IsEnabled || File.Exists(Path.Combine(disabled.Folder, "mod.ini"))) throw new InvalidOperationException("Disable did not hide the runtime descriptor.");
    ModService.SetEnabled(disabled, true);
    if (!Single(ModService.Scan(game)).IsEnabled) throw new InvalidOperationException("Enable did not restore the runtime descriptor.");

    var duplicateRejected = false;
    try { await ModService.ImportAsync(input, game, CancellationToken.None); }
    catch (IOException) { duplicateRejected = true; }
    if (!duplicateRejected) throw new InvalidOperationException("Duplicate Mod ID was accepted.");

    var unsafeZip = Path.Combine(fixture, "unsafe.zip");
    CreateZip(unsafeZip, ("../escaped.txt", "outside"), ("Pack/mod.ini", "[Mod]\nid=Unsafe"));
    var unsafeRejected = false;
    try { await ModService.ImportAsync(unsafeZip, game, CancellationToken.None); }
    catch (InvalidDataException) { unsafeRejected = true; }
    if (!unsafeRejected || File.Exists(Path.Combine(fixture, "escaped.txt"))) throw new InvalidOperationException("ZIP path traversal was not rejected.");

    var parent = Path.Combine(mods, "Nested");
    Directory.CreateDirectory(Path.Combine(parent, "Child"));
    File.WriteAllText(Path.Combine(parent, "mod.ini"), "[Mod]\nid=Parent");
    File.WriteAllText(Path.Combine(parent, "Child", "mod.ini"), "[Mod]\nid=Child");
    var nested = ModService.Scan(game);
    var parentMod = nested.Single(item => item.Id == "Parent");
    var deleteProtected = false;
    try { ModService.Delete(parentMod, nested); }
    catch (IOException) { deleteProtected = true; }
    if (!deleteProtected) throw new InvalidOperationException("Deleting a parent package removed a nested Mod.");
    Directory.Delete(parent, true);

    Console.WriteLine("PASS: ZIP install, metadata, default enabled state, enable/disable, duplicate IDs, path traversal, and nested deletion protection.");
}
finally
{
    var dllTestState = SettingsStore.DllStatePath(Path.Combine(fixture, "fake-game"));
    var dllTestInstall = Path.GetDirectoryName(dllTestState)!;
    if (Directory.Exists(dllTestInstall)) Directory.Delete(dllTestInstall, true);
    if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
}

static ModEntry Single(IEnumerable<ModEntry> items) => items.Single();
static void VerifyCompiledModCacheMigration(string fixtureRoot)
{
    var mod = Path.Combine(fixtureRoot, "legacy-repair-mod");
    Directory.CreateDirectory(Path.Combine(mod, "compiled.blocks"));
    Directory.CreateDirectory(Path.Combine(mod, "meshes"));
    File.WriteAllText(Path.Combine(mod, "mod.ini"), "[Mod]\nid=Legacy\n");
    File.WriteAllText(Path.Combine(mod, "meshes", "petal.mesh"), "author mesh");
    if (CompiledModCache.Detect(mod) != CompiledModCacheKind.Missing)
        throw new InvalidOperationException("A package without a compiled cache was not recognized as a cold build.");
    File.WriteAllBytes(Path.Combine(mod, "compiled.bin"), System.Text.Encoding.ASCII.GetBytes("EFF_MOD_BLOCKS/1"));
    File.WriteAllText(Path.Combine(mod, "compiled.blocks", new string('A', 64) + ".bin"), "old generated block");
    File.WriteAllText(Path.Combine(mod, "source-inputs.bin"), "old source routes");

    if (CompiledModCache.Detect(mod) != CompiledModCacheKind.LegacyOrUnknown)
        throw new InvalidOperationException("An EFF_MOD_BLOCKS/1 package was treated as a reusable current cache.");
    CompiledModCache.ClearDerived(mod);
    if (File.Exists(Path.Combine(mod, "compiled.bin")) || File.Exists(Path.Combine(mod, "source-inputs.bin")) || Directory.Exists(Path.Combine(mod, "compiled.blocks")) ||
        !File.Exists(Path.Combine(mod, "mod.ini")) || !File.Exists(Path.Combine(mod, "meshes", "petal.mesh")))
        throw new InvalidOperationException("Cold migration cleanup did not remove only generated package data.");

    File.WriteAllBytes(Path.Combine(mod, "compiled.bin"), System.Text.Encoding.ASCII.GetBytes("EFF_MOD_BLOCKS/2"));
    if (CompiledModCache.Detect(mod) != CompiledModCacheKind.CurrentBlockStore)
        throw new InvalidOperationException("The current EFF_MOD_BLOCKS/2 cache was not recognized.");
    Console.WriteLine("PASS: old compiled caches are selected for author-driven cold migration while current block stores remain reusable.");
}
static void VerifyExternalDllVersionDetection(string fixtureRoot)
{
    var game = Path.Combine(fixtureRoot, "external-dll-game");
    var plugin = Path.Combine(game, "plugin");
    Directory.CreateDirectory(plugin);
    var source = typeof(GameDiscovery).Assembly.Location;
    var dll = Path.Combine(plugin, "eff.dll");
    File.Copy(source, dll);
    var metadata = FileVersionInfo.GetVersionInfo(source);
    var rawVersion = metadata.ProductVersion;
    if (!Version.TryParse(rawVersion?.Split('+', '-', ' ')[0], out var parsed))
        throw new InvalidOperationException("The test assembly has no parseable product version.");
    var expected = new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0));
    var installed = DllInstallService.InstalledVersion(game);
    if (installed != expected)
        throw new InvalidOperationException("An externally installed DLL version was not detected from file metadata.");

    var settings = new AppSettings { IgnoredManagerVersion = "0.1.1", IgnoredDllVersion = "1.9.0" };
    var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    if (restored.IgnoredManagerVersion != settings.IgnoredManagerVersion || restored.IgnoredDllVersion != settings.IgnoredDllVersion)
        throw new InvalidOperationException("Ignored update versions did not survive settings serialization.");
    Console.WriteLine("PASS: external DLL version detection and ignored-update settings persistence.");
}

static void VerifyUnpackedWorkspaceLookup(string fixtureRoot)
{
    var previousLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    var previousRoot = Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT");
    var unpackedWorkspace = Path.Combine(fixtureRoot, "unpacked-workspace");
    var resourceRoot = Path.Combine(fixtureRoot, "shared-tools");
    var game = Path.Combine(fixtureRoot, "game");
    var indexDirectory = Path.Combine(unpackedWorkspace, "index");
    Directory.CreateDirectory(indexDirectory);
    File.WriteAllText(Path.Combine(indexDirectory, "endfield_assets.eidx"), "test index");
    File.WriteAllText(Path.Combine(indexDirectory, "endfield-index-manifest.json"), "{}");
    Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(fixtureRoot, "local-app-data"));
    Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", null);
    try
    {
        SharedResources.SetResourceRoot(resourceRoot);
        SharedResources.SetIndexWorkspace(unpackedWorkspace);
        var registryPath = Path.Combine(SharedResources.MetadataRoot, "registry.json");
        File.WriteAllText(registryPath, JsonSerializer.Serialize(new
        {
            format = "EFF_SHARED_INDEXES/1",
            entries = new[] { new { game, workspace = unpackedWorkspace } }
        }));

        var selected = EndfieldSharedIndexes.WorkspacePath(SharedResources.IndexWorkspace);
        if (!string.Equals(selected, unpackedWorkspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The user-selected AnimeStudio index workspace was not read from shared settings.");
        if (!EndfieldSharedIndexes.Workspaces(game).Contains(unpackedWorkspace, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The index workspace registered by AnimeStudio was not discovered by Mod Manager.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocal);
        Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", previousRoot);
    }
    Console.WriteLine("PASS: the shared index setting and AnimeStudio game registration are discovered independently of the tools root.");
}

static void VerifySharedResourceSettings(string fixtureRoot)
{
    var previousLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    var previousRoot = Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT");
    var local = Path.Combine(fixtureRoot, "local-app-data");
    var resourceRoot = Path.Combine(fixtureRoot, "selected-resource-root");
    var workspace = Path.Combine(fixtureRoot, "animestudio-workspace");
    try
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
        Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", null);
        SharedResources.SetResourceRoot(resourceRoot);
        if (Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT") is not null)
            throw new InvalidOperationException("Selecting a shared resource root must not pin a process-local override.");
        SharedResources.SetIndexWorkspace(workspace);
        var stored = JsonDocument.Parse(File.ReadAllText(SharedResources.SettingsPath)).RootElement;
        if (stored.GetProperty("resourceRoot").GetString() != resourceRoot ||
            stored.GetProperty("indexWorkspace").GetString() != workspace)
            throw new InvalidOperationException("Shared path settings did not preserve both the tools root and AnimeStudio index workspace.");

        SharedResources.SetResourceRoot(Path.Combine(fixtureRoot, "updated-resource-root"));
        if (SharedResources.ResourceRoot != Path.GetFullPath(Path.Combine(fixtureRoot, "updated-resource-root")))
            throw new InvalidOperationException("A resource root changed by another client was not re-read from shared settings.");
        if (SharedResources.IndexWorkspace != workspace)
            throw new InvalidOperationException("Changing the tools root overwrote the registered AnimeStudio workspace.");

        var digest = new string('a', 64);
        var package = Path.Combine(SharedResources.ResourceRoot, "tools", digest);
        var native = Path.Combine(package, "nativepack");
        Directory.CreateDirectory(native);
        Directory.CreateDirectory(Path.Combine(package, "vendor", "PIL"));
        foreach (var name in new[] { "eff_resource_pack.exe", "eff_source_index.exe", "eff_author_core.dll", "package-protocol.json" })
            File.WriteAllText(Path.Combine(native, name), "fixture");
        File.WriteAllText(Path.Combine(package, "vendor", "PIL", "__init__.py"), "");
        File.WriteAllBytes(Path.Combine(package, "vendor", "PIL", "_imaging.cp311-win_amd64.pyd"), []);
        File.WriteAllBytes(Path.Combine(package, "vendor", "PIL", "_imaging.cp313-win_amd64.pyd"), []);
        File.WriteAllText(Path.Combine(SharedResources.ResourceRoot, "tools", "current.json"), JsonSerializer.Serialize(new
        {
            format = "EFF_SHARED_NATIVE_TOOLS_INSTALL/1",
            sha256 = digest,
            release = "fixture"
        }));
        if (SharedNativeTools.FindInstalledCompiler(SharedResources.ResourceRoot) != Path.Combine(native, "eff_resource_pack.exe"))
            throw new InvalidOperationException("Mod Manager did not discover the shared package prepared by another client.");

        var index = Path.Combine(workspace, "index", "endfield_assets.eidx");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(index, "index");
        if (EndfieldSharedIndexes.WorkspacePath(index) != workspace ||
            EndfieldSharedIndexes.WorkspacePath(Path.GetDirectoryName(index)!) != workspace)
            throw new InvalidOperationException("An index file or index directory did not resolve to its AnimeStudio workspace.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocal);
        Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", previousRoot);
    }
    Console.WriteLine("PASS: the selected tools root, AnimeStudio index workspace, and cross-client shared-tool cache are recognized together.");
}

static async Task VerifyHistoricalIndexLookupAsync()
{
    static IndexDownload Index(string version, char hash)
    {
        var content = "EFF_BUNDLE_CONTENT/1:" + new string(hash, 64);
        return new(new EndfieldIndexRelease("EFF_INDEX_RELEASE/1", version, content, version + ".effindex.gz",
            10, new string('A', 64), 100, new string('B', 64)), "https://github.com/ssice-a/EFF/releases/download/test/index.gz");
    }

    var current = Index("20261022", 'C');
    var historical = Index("20261017", 'F');
    var latestCatalog = new RepairCatalog("EFF_REPAIR_CATALOG/1", "0.3.0", "", [current]);
    var historicalCatalog = new RepairCatalog("EFF_REPAIR_CATALOG/1", "0.2.0", "", [historical]);
    const string source = "https://api.github.com/repos/ssice-a/EFF/releases?per_page=100";
    const string nextPage = "https://api.github.com/repos/ssice-a/EFF/releases?per_page=100&page=2";
    const string latestCatalogUrl = "https://github.com/ssice-a/EFF/releases/download/v1.9.0/eff-index-catalog.json";
    const string historicalCatalogUrl = "https://github.com/ssice-a/EFF/releases/download/v1.8.1/eff-index-catalog.json";
    var releases = JsonSerializer.Serialize(new[]
    {
        new { assets = new[] { new { name = "eff-index-catalog.json", browser_download_url = latestCatalogUrl } } }
    });
    var olderReleases = JsonSerializer.Serialize(new[]
    {
        new { assets = new[] { new { name = "eff-index-catalog.json", browser_download_url = historicalCatalogUrl } } }
    });
    var routes = new Dictionary<string, Func<HttpResponseMessage>>(StringComparer.OrdinalIgnoreCase)
    {
        [source] = () => Response(releases, "<" + nextPage + ">; rel=\"next\""),
        [nextPage] = () => Response(olderReleases),
        [latestCatalogUrl] = () => Response(JsonSerializer.Serialize(latestCatalog)),
        [historicalCatalogUrl] = () => Response(JsonSerializer.Serialize(historicalCatalog))
    };
    using var client = new HttpClient(new HttpRouteHandler(routes));
    var match = await EndfieldIndexUpdates.Find(source, historical.Release.GameContent, CancellationToken.None, client);
    if (match != historical) throw new InvalidOperationException("An index from an older EFF Release was not selected.");
    var missing = await EndfieldIndexUpdates.Find(source, "EFF_BUNDLE_CONTENT/1:" + new string('0', 64), CancellationToken.None, client);
    if (missing != current)
        throw new InvalidOperationException("The newest published index was not selected when there was no exact content match.");
    var publishedRows = new[] { new BundleContentInfo("a.ab", new string('A', 32), 10), new BundleContentInfo("b.ab", new string('B', 32), 20) };
    var installedRows = new[] { new BundleContentInfo("A.ab", new string('C', 32), 11), new BundleContentInfo("b.ab", new string('D', 32), 21) };
    if (!RepairCatalogLookup.BundlePathsCompatible(publishedRows, installedRows))
        throw new InvalidOperationException("Bundle content metadata differences blocked an otherwise compatible path set.");
    if (RepairCatalogLookup.BundlePathsCompatible(publishedRows, [installedRows[0], new BundleContentInfo("c.ab", new string('D', 32), 21)]))
        throw new InvalidOperationException("A different Bundle path set was accepted.");
    Console.WriteLine("PASS: historical exact match, latest-index fallback, and Bundle-path compatibility.");
}

static HttpResponseMessage Response(string json, string? link = null)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    if (link != null) response.Headers.Add("Link", link);
    return response;
}

static void Equal(string expected, string actual, string label)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException(label + ": expected '" + expected + "', got '" + actual + "'.");
}
static void CreateZip(string path, params (string Name, string Text)[] entries)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (name, text) in entries)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }
}

static async Task VerifyOfficialDllInstallAsync(string fixtureRoot)
{
    var gameRoot = Path.Combine(fixtureRoot, "fake-game");
    var pluginRoot = Path.Combine(gameRoot, "plugin");
    Directory.CreateDirectory(Path.Combine(gameRoot, "Endfield_Data"));
    Directory.CreateDirectory(pluginRoot);
    File.WriteAllText(Path.Combine(gameRoot, "Endfield.exe"), "game");
    File.WriteAllText(Path.Combine(gameRoot, "GameAssembly.dll"), "assembly");
    File.WriteAllText(Path.Combine(pluginRoot, "eff.dll"), "original dll");
    File.WriteAllText(Path.Combine(pluginRoot, "eff.ini"), "user configuration");
    File.WriteAllText(Path.Combine(pluginRoot, "package-protocol.json"), "original protocol");
    File.WriteAllText(Path.Combine(gameRoot, "d3dcompiler_47.dll"), "original proxy");

    var version = await DllInstallService.InstallOrUpdateAsync(gameRoot, null, CancellationToken.None);
    if (version < new Version(1, 9, 0)) throw new InvalidOperationException("Unexpected EFF DLL version: " + version);
    if (File.ReadAllText(Path.Combine(pluginRoot, "eff.ini")) != "user configuration") throw new InvalidOperationException("User configuration was overwritten.");
    if (File.ReadAllText(Path.Combine(pluginRoot, "eff.dll")) == "original dll") throw new InvalidOperationException("EFF DLL was not installed.");
    if (!DllInstallService.IsManaged(gameRoot)) throw new InvalidOperationException("Install manifest was not written.");

    File.WriteAllText(Path.Combine(pluginRoot, "eff.dll"), "external replacement");
    File.Delete(Path.Combine(pluginRoot, "package-protocol.json"));
    var outcome = DllInstallService.Uninstall(gameRoot);
    if (File.ReadAllText(Path.Combine(pluginRoot, "eff.dll")) != "external replacement") throw new InvalidOperationException("Uninstall removed a file modified externally.");
    if (File.ReadAllText(Path.Combine(pluginRoot, "package-protocol.json")) != "original protocol") throw new InvalidOperationException("Original protocol file was not restored.");
    if (File.ReadAllText(Path.Combine(gameRoot, "d3dcompiler_47.dll")) != "original proxy") throw new InvalidOperationException("Original root proxy was not restored.");
    if (File.ReadAllText(Path.Combine(pluginRoot, "eff.ini")) != "user configuration") throw new InvalidOperationException("User configuration changed during uninstall.");
    if (DllInstallService.IsManaged(gameRoot)) throw new InvalidOperationException("Uninstall manifest was not removed.");
    Console.WriteLine("PASS: official EFF v" + version + " asset SHA-256, install, config preservation, backup restore, and modified-file preservation (" + outcome + ").");
}

static async Task VerifyOfficialRepairAssetAsync(string fixtureRoot)
{
    var (release, asset) = await GitHubReleaseService.GetLatestRepairAssetAsync(CancellationToken.None);
    if (release is null || asset is null) throw new InvalidOperationException("EFF Releases does not contain the Repair tool.");
    var version = GitHubReleaseService.RepairVersion(asset.name);
    if (version < new Version(0, 3, 0)) throw new InvalidOperationException("Repair is older than the EFF v1.9-compatible release: " + version);
    var package = Path.Combine(fixtureRoot, asset.name);
    await GitHubReleaseService.DownloadVerifiedAsync(asset, package, null, CancellationToken.None);
    using var archive = ZipFile.OpenRead(package);
    var paths = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!paths.Contains("EFF.Repair.exe") || !paths.Contains("native/eff_resource_pack.exe"))
        throw new InvalidOperationException("The official Repair package is missing its app or compiler.");
    Console.WriteLine("PASS: EFF Release " + release.tag_name + " Repair v" + version + " asset SHA-256 and compiler payload.");
}

static async Task VerifyOfficialUpdateChecksAsync(string fixtureRoot)
{
    var dllVersion = await DllInstallService.GetLatestVersionAsync(CancellationToken.None)
        ?? throw new InvalidOperationException("EFF Releases has no recognizable DLL update asset.");
    var manager = await ManagerUpdateService.CheckAsync(CancellationToken.None);
    Console.WriteLine("PASS: GitHub update check found EFF v" + dllVersion + ".");
    if (manager.Release is null)
    {
        Console.WriteLine("INFO: manager update check reached GitHub, but the repository has no Latest Release to download.");
        return;
    }
    if (manager.Asset is null || manager.Version is null)
    {
        Console.WriteLine("INFO: manager release " + manager.Release.tag_name + " has no matching Windows update package.");
        return;
    }

    var package = Path.Combine(fixtureRoot, manager.Asset.name);
    var digest = await GitHubReleaseService.DownloadVerifiedAsync(manager.Asset, package, null, CancellationToken.None);
    using var archive = ZipFile.OpenRead(package);
    var paths = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!paths.Contains("EFF.ModManager.exe") || paths.Contains("native/eff_resource_pack.exe"))
        throw new InvalidOperationException("The manager update package must contain the app and obtain the compiler from EFF shared tools.");
    Console.WriteLine("PASS: manager v" + manager.Version + " update package size/SHA-256 without a duplicate compiler (" + digest + ").");
}

static async Task VerifyLiveRepairAsync(string fixtureRoot)
{
    var settings = SettingsStore.Load();
    var game = Path.GetFullPath(settings.GamePath);
    var resourceRoot = Path.GetFullPath(settings.ResourceRoot);
    var originalMod = Path.Combine(ModService.ModsRoot(game), "LZY");
    var externalDll = Path.Combine(game, "plugin", "eff.dll");
    if (!GameDiscovery.IsGame(game) || !Directory.Exists(originalMod) || !File.Exists(externalDll))
        throw new InvalidOperationException("The configured game, manually installed EFF DLL, or LZY test Mod was not found.");
    if (DllInstallService.IsManaged(game))
        throw new InvalidOperationException("This integration test requires the EFF DLL to be installed manually, without a manager manifest.");

    var workspace = Path.Combine(fixtureRoot, "repair-workspace");
    var workspaceIndex = Path.Combine(workspace, "index");
    Directory.CreateDirectory(workspaceIndex);
    foreach (var name in new[] { "endfield_assets.eidx", "endfield_assets.eidx.bundles", "endfield-index-manifest.json" })
        File.Copy(Path.Combine(resourceRoot, "index", name), Path.Combine(workspaceIndex, name));

    var modCopy = Path.Combine(fixtureRoot, "repair-mod", "LZY");
    CopyDirectoryForTest(originalMod, modCopy);
    File.WriteAllBytes(Path.Combine(modCopy, "compiled.bin"), System.Text.Encoding.ASCII.GetBytes("EFF_MOD_BLOCKS/1"));
    File.Delete(Path.Combine(modCopy, "source-inputs.bin"));
    var oldBlocks = Path.Combine(modCopy, "compiled.blocks");
    if (Directory.Exists(oldBlocks)) Directory.Delete(oldBlocks, recursive: true);
    var appAssemblyPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "EFF.ModManager", "bin", "Release", "net9.0-windows", "EFF.ModManager.dll"));
    var app = System.Reflection.Assembly.LoadFrom(appAssemblyPath);
    var archiveType = app.GetType("AnimeStudio.GUI.EndfieldVfsArchive", throwOnError: true)!;
    var archive = archiveType.GetMethod("Open", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
        .Invoke(null, [game])!;
    var builder = app.GetType("AnimeStudio.GUI.EndfieldVfsAssetIndexBuilder", throwOnError: true)!;
    var bind = builder.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        .Single(method => method.Name == "TryGetOrRebindCurrentIndex" && method.GetParameters().Length == 4);
    var bindArguments = new object?[] { archive, workspace, null, CancellationToken.None };
    if (!(bool)bind.Invoke(null, bindArguments)!)
        throw new InvalidOperationException("The copied unpacker index cannot be bound to the installed game's Bundle content.");

    var stateMethod = app.GetType("EffRepair.RepairService", throwOnError: true)!
        .GetMethod("StatePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var receipt = (string)stateMethod.Invoke(null, [modCopy])!;
    var receiptExisted = File.Exists(receipt);
    var progressLines = new List<string>();
    var progress = new InlineProgress<string>(progressLines.Add);
    var repairService = app.GetType("EffModManager.IntegratedRepairService", throwOnError: true)!;
    var repair = repairService.GetMethod("RepairAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var task = (Task)repair.Invoke(null, [game, workspace, new[] { modCopy }, progress, CancellationToken.None])!;
    try
    {
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var repaired = (int)result.GetType().GetProperty("Repaired")!.GetValue(result)!;
        var failed = (int)result.GetType().GetProperty("Failed")!.GetValue(result)!;
        if (repaired != 1 || failed != 0 || !File.Exists(Path.Combine(modCopy, "compiled.bin")) || !File.Exists(Path.Combine(modCopy, "source-inputs.bin")) ||
            CompiledModCache.Detect(modCopy) != CompiledModCacheKind.CurrentBlockStore)
            throw new InvalidOperationException("Integrated repair did not complete successfully: " + string.Join(" | ", progressLines.Where(line => !line.Contains("COMPILED-MOD-COST", StringComparison.Ordinal))));
        Console.WriteLine("PASS: integrated repair cold-migrated a copied legacy LZY package from the current index; the installed Mod was left untouched.");
    }
    finally
    {
        if (!receiptExisted && File.Exists(receipt)) File.Delete(receipt);
    }
}

static void CopyDirectoryForTest(string source, string destination)
{
    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("The live repair test refuses to copy a reparse point: " + source);
    Directory.CreateDirectory(destination);
    foreach (var entry in Directory.EnumerateFileSystemEntries(source))
    {
        if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The live repair test refuses to copy a reparse point: " + entry);
        var target = Path.Combine(destination, Path.GetFileName(entry));
        if (Directory.Exists(entry)) CopyDirectoryForTest(entry, target);
        else File.Copy(entry, target);
    }
}

sealed class HttpRouteHandler(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == null || !routes.TryGetValue(request.RequestUri.AbsoluteUri, out var response))
            throw new InvalidOperationException("Unexpected HTTP request: " + request.RequestUri);
        return Task.FromResult(response());
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
