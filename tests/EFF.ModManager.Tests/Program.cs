using System.IO.Compression;
using EffModManager;

var fixture = Path.Combine(Path.GetTempPath(), "effmm-tests-" + Guid.NewGuid().ToString("N"));
var game = Path.Combine(fixture, "game");
var mods = ModService.ModsRoot(game);
var input = Path.Combine(fixture, "sample-pack.zip");
Directory.CreateDirectory(mods);
Directory.CreateDirectory(Path.GetDirectoryName(input)!);

try
{
    Directory.CreateDirectory(Path.Combine(game, "Endfield_Data"));
    File.WriteAllText(Path.Combine(game, "Endfield.exe"), "game");
    File.WriteAllText(Path.Combine(game, "GameAssembly.dll"), "assembly");
    if (!GameDiscovery.IsGame(game) || DllInstallService.IsManaged(game))
        throw new InvalidOperationException("A valid game without a manager-installed EFF DLL was rejected.");

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
