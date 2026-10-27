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
            var shared = await Task.Run(() => EndfieldSharedIndexes.FindCurrent(archive, token,
                EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive))), token);
            var workspace = shared ?? EndfieldSharedIndexes.DefaultWorkspace(EndfieldSharedIndexes.Game(archive));
            if (string.IsNullOrWhiteSpace(workspace))
                throw new IOException("没有找到 AnimeStudio 登记的索引工作区。请先在 AnimeStudio 准备索引并登记工作区；完整索引不会写入 C 盘。");
            var index = EndfieldVfsAssetIndexBuilder.GetIndexPath(workspace);

            var content = await Task.Run(() => EndfieldPortableIndex.ContentIdentity(archive), token);
            var compiler = await SharedNativeTools.GetCompilerAsync(resourceRoot, token);
            var selected = mods.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var repairProgress = new Progress<string>(value => progress?.Report(value));
            var completed = 0;
            var failed = 0;
            foreach (var mod in selected)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await RepairService.Repair(mod, game, index, content, compiler, repairProgress, token);
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
