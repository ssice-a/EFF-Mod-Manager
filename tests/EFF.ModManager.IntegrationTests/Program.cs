using System.Security.Cryptography;
using System.Text.Json;
using EffModManager;
using AnimeStudio;
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
    await CatalogCacheTests.RunAsync(root);
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
    var vfs = Path.Combine(game, "Endfield_Data", "StreamingAssets", "VFS");
    Directory.CreateDirectory(vfs);
    File.WriteAllText(Path.Combine(game, "Endfield.exe"), "fixture");
    File.WriteAllText(Path.Combine(game, "GameAssembly.dll"), "fixture");
    IndexFixture.WriteVfs(vfs);
    var offline = new OfflineHandler();
    using var client = new HttpClient(offline);
    var archive = EndfieldVfsArchive.Open(game);
    var workspace = Path.Combine(root, "publisher-workspace");
    await EndfieldVfsAssetIndexBuilder.BuildAsync(archive, workspace,
        GameManager.GetGameByType(GameType.ArknightsEndfield), new InlineIndexProgress(), CancellationToken.None);
    Require(EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(archive, workspace, out var index), "Publisher fixture did not produce a validated local index.");
    var loaded = EndfieldIndexStore.Load(index);
    Require(loaded.StoredAssetCount == 1 && loaded.Assets.Records.Any(item => item.Name == "OfflineFixture"),
        "Publisher fixture did not parse the real UnityFS/SerializedFile fixture through the bundled reader.");
    var span = EndfieldBundleIndexCache.TryLoad(index)!.Spans.Values.Single();
    Require(EndfieldIndexStore.ReadBundleAssets(index, span, default).Single().Name == "OfflineFixture",
        "Bounded Bundle lookup did not reuse the incremental index range.");
    var corruptRangeRejected = false;
    try { EndfieldIndexStore.ReadBundleAssets(index, span with { Sha256 = new byte[32] }, default); }
    catch (InvalidDataException) { corruptRangeRejected = true; }
    Require(corruptRangeRejected, "Corrupt Bundle index range was accepted.");
    Console.WriteLine("PASS: publisher fixture builds a complete index from local game VFS for package tests.");

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

    var previousCatalogSource = Environment.GetEnvironmentVariable("EFF_INDEX_CATALOG");
    try
    {
        Environment.SetEnvironmentVariable("EFF_INDEX_CATALOG", "https://api.github.com/repos/ssice-a/EFF/releases?per_page=100");
        var publishedHandler = new PublishedIndexHandler(package, release);
        using var publishedClient = new HttpClient(publishedHandler);
        var publishedGame = Path.Combine(root, "published-game");
        var publishedVfs = Path.Combine(publishedGame, "Endfield_Data", "StreamingAssets", "VFS");
        Directory.CreateDirectory(publishedVfs);
        IndexFixture.WriteVfs(publishedVfs);
        File.WriteAllText(Path.Combine(publishedGame, "Endfield.exe"), "fixture");
        var publishedResources = Path.Combine(root, "published-index-resources");
        var publishedWorkspace = await IndexAcquisition.ResolveAsync(publishedGame, "", publishedResources, null,
            CancellationToken.None, publishedClient);
        Require(publishedHandler.PackageDownloads == 1,
            $"Expected one published package download, got {publishedHandler.PackageDownloads} from {publishedHandler.Requests} requests.");
        Require(EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(EndfieldVfsArchive.Open(publishedGame), publishedWorkspace, out _),
            "A downloaded GitHub index was not imported into a validated workspace.");
        var savedRequests = publishedHandler.Requests;
        await IndexAcquisition.ResolveAsync(publishedGame, "", publishedResources, null, CancellationToken.None, publishedClient);
        Require(publishedHandler.Requests == savedRequests, "A valid imported workspace triggered another GitHub lookup.");

        var manualGame = Path.Combine(root, "manual-recovery-game");
        var manualVfs = Path.Combine(manualGame, "Endfield_Data", "StreamingAssets", "VFS");
        Directory.CreateDirectory(manualVfs);
        IndexFixture.WriteVfs(manualVfs);
        var manualResourcesAfterFailure = Path.Combine(root, "manual-recovery-resources");
        publishedHandler.FailPackageDownload = true;
        try
        {
            await IndexAcquisition.ResolveAsync(manualGame, "", manualResourcesAfterFailure, null,
                CancellationToken.None, publishedClient);
            throw new InvalidOperationException("A failed GitHub package download was reported as a successful import.");
        }
        catch (HttpRequestException error)
        {
            Require(error.Data[nameof(RecoveryAdvice)] is RecoveryAdvice advice &&
                advice.Links.Contains(PublishedIndexHandler.DownloadUrl),
                "Package download failure did not expose the exact download URL.");
        }
        var manualFolder = Path.Combine(manualResourcesAfterFailure, "indexes");
        Directory.CreateDirectory(manualFolder);
        File.Copy(package, Path.Combine(manualFolder, "manual-download.effindex.gz"));
        savedRequests = publishedHandler.Requests;
        var manualRecovery = await IndexAcquisition.ResolveAsync(manualGame, "", manualResourcesAfterFailure, null,
            CancellationToken.None, publishedClient);
        Require(publishedHandler.Requests == savedRequests && EndfieldVfsAssetIndexBuilder.TryGetCurrentIndex(
            EndfieldVfsArchive.Open(manualGame), manualRecovery, out _),
            "Retry after a failed download did not import the manually placed archive offline.");
    }
    finally { Environment.SetEnvironmentVariable("EFF_INDEX_CATALOG", previousCatalogSource); }
    Console.WriteLine("PASS: an exact GitHub catalog entry downloads and imports once, then reuses the validated local workspace.");
    Console.WriteLine("PASS: a failed GitHub package download exposes the exact URL and retry imports the manually placed package offline.");

    var unpublishedGame = Path.Combine(root, "unpublished-game");
    var unpublishedVfs = Path.Combine(unpublishedGame, "Endfield_Data", "StreamingAssets", "VFS");
    Directory.CreateDirectory(unpublishedVfs);
    IndexFixture.WriteVfs(unpublishedVfs);
    using var notFound = new HttpClient(new MissingCatalogueHandler());
    var unpublishedResources = Path.Combine(root, "unpublished-resources");
    var unpublishedFailed = false;
    try
    {
        await IndexAcquisition.ResolveAsync(unpublishedGame, "", unpublishedResources, null,
            CancellationToken.None, notFound);
    }
    catch (FileNotFoundException error)
    {
        unpublishedFailed = error.Data[nameof(RecoveryAdvice)] is RecoveryAdvice advice &&
            advice.Links.Contains(RecoveryHelp.EffIndexCatalog) && advice.Links.Contains(RecoveryHelp.EffIndexRequest);
    }
    var unpublishedArchive = EndfieldVfsArchive.Open(unpublishedGame);
    var unpublishedWorkspace = EndfieldSharedIndexes.DefaultWorkspace(
        EndfieldSharedIndexes.Game(unpublishedArchive), unpublishedResources);
    var unpublishedIndex = EndfieldVfsAssetIndexBuilder.GetIndexPath(unpublishedWorkspace);
    Require(unpublishedFailed && !File.Exists(unpublishedIndex),
        "Missing published game content did not provide GitHub recovery links or incorrectly built a local index.");
    Console.WriteLine("PASS: unpublished game content reports GitHub recovery without local index construction.");
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

sealed class InlineIndexProgress : IProgress<EndfieldIndexProgress>
{
    public void Report(EndfieldIndexProgress value) { }
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

sealed class PublishedIndexHandler(string package, EndfieldIndexRelease release) : HttpMessageHandler
{
    internal const string DownloadUrl = "https://github.com/ssice-a/EFF/releases/download/fixture/index.effindex.gz";
    private readonly RepairCatalog _catalog = new("EFF_REPAIR_CATALOG/1", "fixture", "", [
        new IndexDownload(release, DownloadUrl)]);
    internal int Requests { get; private set; }
    internal int PackageDownloads { get; private set; }
    internal bool FailPackageDownload { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
        {
            var response = new[] { new { assets = new[] { new { name = "eff-index-catalog.json", browser_download_url = "https://github.com/ssice-a/EFF/releases/download/fixture/eff-index-catalog.json" } } } };
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(response)));
        }
        if (path.EndsWith("eff-index-catalog.json", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(_catalog)));
        if (path.EndsWith("index.effindex.gz", StringComparison.OrdinalIgnoreCase))
        {
            PackageDownloads++;
            if (FailPackageDownload) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(package)) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };
}
