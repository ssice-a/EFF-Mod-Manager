using AnimeStudio.GUI;
using EffRepair;

namespace EffModManager;

internal static class IntegratedRepairService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<RepairRunSummary> RepairAsync(string game, string resourceRoot, IReadOnlyList<string> mods,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!GameDiscovery.IsGame(game)) throw new IOException("请选择有效的 Endfield 游戏目录。");
        if (mods.Count == 0) throw new IOException("请先勾选需要修复的 Mod。");

        await Gate.WaitAsync(token);
        var previousRoot = Environment.GetEnvironmentVariable("EFF_RESOURCE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", Path.GetFullPath(resourceRoot));
            var archive = await Task.Run(() => EndfieldVfsArchive.Open(game), token);
            var content = await Task.Run(() => EndfieldPortableIndex.ContentIdentity(archive), token);
            var localWorkspace = Path.Combine(SettingsStore.DataRoot, "repair", SettingsStore.GameKey(game), "workspace");
            var shared = await Task.Run(() => EndfieldSharedIndexes.FindCurrent(archive, token,
                localWorkspace, EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive))), token);
            var workspace = shared ?? EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive));
            var index = EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);

            if (!await Task.Run(() => EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out _, token), token))
            {
                progress?.Report("正在 GitHub 查找与当前游戏资源匹配的完整索引…");
                var item = await EndfieldIndexUpdates.Find(EndfieldIndexUpdates.DefaultSource, content, token)
                    ?? throw new IOException("仓库尚未发布匹配当前游戏资源的索引，请等待更新。");
                var package = EndfieldIndexUpdates.CachedPackage(item);
                await EndfieldIndexUpdates.Download(item, package, progress ?? new Progress<string>(_ => { }), token);
                progress?.Report("正在校验并导入索引…");
                await Task.Run(() => EndfieldPortableIndex.Import(archive, workspace, package, token), token);
                if (!await Task.Run(() => EndfieldSharedIndexes.TryRemember(archive, workspace, token), token))
                    progress?.Report("索引已导入；共享位置登记失败。");
            }

            if (!await Task.Run(() => EndfieldVfsAssetIndexBuilder.TryGetOrRebindCurrentIndex(archive, workspace, out _, token), token))
                throw new IOException("当前索引无法通过游戏内容校验，未修复任何 Mod。");

            var selected = mods.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var repairProgress = new Progress<string>(value => progress?.Report(value));
            var completed = 0;
            var failed = 0;
            foreach (var mod in selected)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await RepairService.Repair(mod, game, index, content, repairProgress, token);
                    progress?.Report(result);
                    completed++;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failed++;
                    progress?.Report(Path.GetFileName(mod) + "：" + error.Message);
                }
            }

            return new RepairRunSummary(completed, failed, workspace);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EFF_RESOURCE_ROOT", previousRoot);
            Gate.Release();
        }
    }
}
