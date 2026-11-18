using System.Security.Cryptography;
using EffModManager;
using AnimeStudio.GUI;
using System.Net;

var root = Path.Combine(Path.GetTempPath(), "effmm-integration-" + Guid.NewGuid().ToString("N"));
var previousLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
var previousResources = Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT");
Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "metadata"));
Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", Path.Combine(root, "resources"));
Directory.CreateDirectory(root);
try
{
    WeaponTests.Run(root);
    CharacterTargetTests.Run(root);
    CatalogCacheTests.Run(root);
    await VerifyFormat3UiRepairAsync(root);
    await VerifyOfflineIndexAsync(root);
    VerifySourceReaderDependencies();
    ManagerUiTests.Run(root);
}
finally
{
    Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocal);
    Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", previousResources);
    Directory.Delete(root, true);
}

static async Task VerifyFormat3UiRepairAsync(string root)
{
    var game = Path.Combine(root, "game");
    Directory.CreateDirectory(Path.Combine(game, "Endfield_Data"));
    File.WriteAllText(Path.Combine(game, "Endfield.exe"), "fixture");
    File.WriteAllText(Path.Combine(game, "GameAssembly.dll"), "fixture");
    var mod = Path.Combine(ModService.ModsRoot(game), "AuthorCopy");
    Directory.CreateDirectory(Path.Combine(mod, "meshes"));
    Directory.CreateDirectory(Path.Combine(mod, "textures"));
    // Payload contents are deliberately not opened by declaration validation.
    File.WriteAllText(Path.Combine(mod, "meshes", "body.effres"), "native payload is validated on demand");
    File.WriteAllText(Path.Combine(mod, "textures", "color.dds"), "ordinary DDS is validated on demand");
    var manifest = """
        [Mod]
        format=3
        id=SharedAuthor
        controls=ini
        [MeshBody]
        path=meshes/body.effres
        [TextureColor]
        dds=textures/color.dds
        source.path=assets/textures/color.png
        source.asset=Color
        [PartBody]
        mesh=MeshBody
        visible=1
        [ReplaceBody]
        target.path=assets/models/body.fbx
        target.asset=Body
        parts=PartBody
        """;
    File.WriteAllText(Path.Combine(mod, "mod.ini"), manifest);
    var before = Snapshot(mod);
    var messages = new List<string>();
    var progress = new InlineProgress(messages.Add);
    // This is the service called by the WPF Repair button. No VFS, index,
    // AnimeStudio installation, shared-tools cache or network is available.
    var result = await IntegratedRepairService.RepairAsync(game, "", [mod], progress, CancellationToken.None);
    Require(result.Repaired == 1 && result.Failed == 0, "Current Format 3 repair must validate without an index/compiler/network: " + string.Join("\n", messages));
    Require(before.SequenceEqual(Snapshot(mod)), "Validation changed a Mod file or generated a compiled cache.");
    Require(messages.Any(value => value.Contains("按需", StringComparison.Ordinal)), "Validation did not disclose the demand-validation scope.");

    var entry = ModService.ScanTree(mod).Single();
    ModService.SetEnabled(entry, false);
    var disabledBefore = Snapshot(mod);
    result = await IntegratedRepairService.RepairAsync(game, "", [mod], progress, CancellationToken.None);
    Require(result.Repaired == 1 && result.Failed == 0 && disabledBefore.SequenceEqual(Snapshot(mod)),
        "Disabled Format 3 validation renamed or changed its descriptor.");
    ModService.SetEnabled(ModService.ScanTree(mod).Single(), true);

    File.Delete(Path.Combine(mod, "textures", "color.dds"));
    messages.Clear();
    result = await IntegratedRepairService.RepairAsync(game, "", [mod], progress, CancellationToken.None);
    Require(result.Failed == 1 && result.Repaired == 0 && messages.Any(value => value.Contains("color.dds", StringComparison.Ordinal)),
        "Missing declared DDS was not reported precisely through the UI service.");
    Require(!File.Exists(Path.Combine(mod, "textures", "color.dds")), "Missing authored pixels were silently invented.");
    File.WriteAllText(Path.Combine(mod, "textures", "color.dds"), "ordinary DDS is validated on demand");
    File.AppendAllText(Path.Combine(mod, "mod.ini"), "\n[ParameterInvalid]\nvariable=$x\ndefault=nan\n");
    messages.Clear();
    result = await IntegratedRepairService.RepairAsync(game, "", [mod], progress, CancellationToken.None);
    Require(result.Failed == 1 && result.Repaired == 0, "Invalid INI control declarations bypassed the shared compiler.");
    File.WriteAllText(Path.Combine(mod, "mod.ini"), manifest.Replace("meshes/body.effres", "../outside.effres", StringComparison.Ordinal));
    result = await IntegratedRepairService.RepairAsync(game, "", [mod], progress, CancellationToken.None);
    Require(result.Failed == 1 && result.Repaired == 0, "Resource traversal bypassed the shared manifest validator.");
    Console.WriteLine("PASS: actual UI Format 3 repair validates current declarations and paths, reports missing resources, compiles controls, and never builds legacy caches.");
}

static string[] Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Order(StringComparer.OrdinalIgnoreCase)
    .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    .ToArray();

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static async Task VerifyOfflineIndexAsync(string root)
{
    var game = Path.Combine(root, "offline-game");
    var resources = Path.Combine(root, "offline-resources");
    var vfs = Path.Combine(game, "Endfield_Data", "StreamingAssets", "VFS");
    Directory.CreateDirectory(vfs);
    File.WriteAllText(Path.Combine(game, "Endfield.exe"), "fixture");
    File.WriteAllText(Path.Combine(game, "GameAssembly.dll"), "fixture");
    IndexFixture.WriteVfs(vfs);
    var offline = new OfflineHandler();
    using var client = new HttpClient(offline);
    var messages = new List<string>();
    var workspace = await LegacyMigrationService.ResolveWorkspaceAsync(game, "", resources,
        new InlineProgress(messages.Add), CancellationToken.None, client);
    var archive = EndfieldVfsArchive.Open(game);
    Require(EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, workspace, out var index), "Offline fallback did not publish a validated local index.");
    var loaded = EndfieldIndexStore.Load(index);
    Require(loaded.StoredAssetCount == 1 && loaded.Assets.Records.Any(item => item.Name == "OfflineFixture"),
        "Offline fallback did not parse the real UnityFS/SerializedFile fixture through the bundled reader.");
    var span = EndfieldBundleIndexCache.TryLoad(index)!.Spans.Values.Single();
    Require(EndfieldIndexStore.ReadBundleAssets(index, span, default).Single().Name == "OfflineFixture",
        "Bounded Bundle lookup did not reuse the incremental index range.");
    var corruptRangeRejected = false;
    try { EndfieldIndexStore.ReadBundleAssets(index, span with { Sha256 = new byte[32] }, default); }
    catch (InvalidDataException) { corruptRangeRejected = true; }
    Require(corruptRangeRejected, "Corrupt Bundle index range was accepted.");
    Console.WriteLine("PASS: actual migration resolver builds and validates a fresh index from local game VFS when network is unavailable.");

    var package = Path.Combine(root, "manual.effindex.gz");
    var release = EndfieldPortableIndex.Pack(archive, workspace, package, "fixture");
    var manualResources = Path.Combine(root, "manual-index-resources");
    var manualWorkspace = await IndexAcquisition.ResolveAsync(game, package, manualResources, null, CancellationToken.None, client);
    Require(manualWorkspace.StartsWith(manualResources, StringComparison.OrdinalIgnoreCase) &&
        EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, manualWorkspace, out _),
        "Explicit manually downloaded package did not import into its selected resource root.");
    var saved = Snapshot(manualWorkspace);
    var bad = Path.Combine(root, "bad.effindex.gz");
    File.WriteAllText(bad, "not a portable index");
    var rejected = false;
    try { await IndexAcquisition.ResolveAsync(game, bad, manualResources, null, CancellationToken.None, client); }
    catch (InvalidDataException) { rejected = true; }
    Require(rejected && saved.SequenceEqual(Snapshot(manualWorkspace)), "Invalid explicit package silently reused another index or altered the valid workspace.");
    Console.WriteLine("PASS: explicit portable-index import is offline, validates actual envelope and game identity, and retains the committed workspace on corruption.");

    var cachedGame = Path.Combine(root, "cached-game");
    var cachedVfs = Path.Combine(cachedGame, "Endfield_Data", "StreamingAssets", "VFS");
    Directory.CreateDirectory(cachedVfs);
    IndexFixture.WriteVfs(cachedVfs);
    var cachedResources = Path.Combine(root, "cached-resources");
    Directory.CreateDirectory(cachedResources);
    File.Copy(package, Path.Combine(cachedResources, "downloaded.effindex.gz"));
    var calls = offline.Requests;
    var cachedWorkspace = await IndexAcquisition.ResolveAsync(cachedGame, "", cachedResources, null, CancellationToken.None, client);
    Require(offline.Requests == calls && EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(EndfieldVfsArchive.Open(cachedGame), cachedWorkspace, out _),
        "A manually downloaded local package required a public catalogue request.");
    Console.WriteLine("PASS: a locally downloaded portable index is detected and imported before any network request.");

    var unpublishedGame = Path.Combine(root, "unpublished-game");
    var unpublishedVfs = Path.Combine(unpublishedGame, "Endfield_Data", "StreamingAssets", "VFS");
    Directory.CreateDirectory(unpublishedVfs);
    IndexFixture.WriteVfs(unpublishedVfs);
    using var notFound = new HttpClient(new MissingCatalogueHandler());
    var unpublishedWorkspace = await IndexAcquisition.ResolveAsync(unpublishedGame, "", Path.Combine(root, "unpublished-resources"), null, CancellationToken.None, notFound);
    Require(EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(EndfieldVfsArchive.Open(unpublishedGame), unpublishedWorkspace, out _),
        "No matching public index prevented a local build.");
    Console.WriteLine("PASS: unpublished game content falls back to the bundled local index builder.");
}

static void VerifySourceReaderDependencies()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EFF.ModManager.SourceReader", "EFF.ModManager.SourceReader.csproj")))
        directory = directory.Parent;
    Require(directory is not null, "Cannot find the Manager source root for dependency contract verification.");
    var source = Path.GetFullPath(Path.Combine(directory!.FullName, "..", "tools", "AnimeStudio", "AnimeStudio", "AnimeStudio.csproj"));
    var bundled = Path.Combine(directory.FullName, "EFF.ModManager.SourceReader", "EFF.ModManager.SourceReader.csproj");
    static string[] Packages(string path) => System.Xml.Linq.XDocument.Load(path).Descendants("PackageReference")
        .Select(value => value.Attribute("Include")!.Value + "=" + value.Attribute("Version")!.Value).Order(StringComparer.Ordinal).ToArray();
    Require(Packages(source).SequenceEqual(Packages(bundled)), "Bundled source-reader dependencies drifted from the linked AnimeStudio core.");
    Console.WriteLine("PASS: bundled source-reader dependency versions match the core source project.");
}

sealed class InlineProgress(Action<string> write) : IProgress<string>
{
    public void Report(string value) => write(value);
}

sealed class OfflineHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        throw new HttpRequestException("fixture network unavailable");
    }
}

sealed class MissingCatalogueHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}
