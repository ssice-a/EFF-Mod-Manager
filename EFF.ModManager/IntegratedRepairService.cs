namespace EffModManager;

internal static class IntegratedRepairService
{
    internal static Task<RepairRunSummary> RepairAsync(string game, string resourceRoot, IReadOnlyList<string> mods,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!GameDiscovery.IsGame(game)) throw new IOException("请选择有效的 Endfield 游戏目录。");
        if (mods.Count == 0) throw new IOException("请先勾选需要检查的 Mod。");
        var completed = 0;
        var failed = 0;
        foreach (var mod in mods.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(Format3Validation.Validate(mod, token));
                completed++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed++;
                progress?.Report(Path.GetFileName(mod) + "：" + error.Message);
            }
        }
        return Task.FromResult(new RepairRunSummary(completed, failed, resourceRoot));
    }
}
