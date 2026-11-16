namespace EffModManager;

internal sealed record UpdateCheckResult<T>(T? Value, Exception? Error)
{
    internal bool Succeeded => Error is null;
}

internal static class UpdateCheckService
{
    internal static async Task<(UpdateCheckResult<Version?> Dll,
        UpdateCheckResult<(GitHubRelease? Release, GitHubAsset? Asset, Version? Version)> Manager)> CheckAsync(
        Func<CancellationToken, Task<Version?>> dll,
        Func<CancellationToken, Task<(GitHubRelease? Release, GitHubAsset? Asset, Version? Version)>> manager,
        CancellationToken token, TimeSpan? deadline = null)
    {
        var dllTask = CheckOneAsync(dll, token, deadline ?? TimeSpan.FromSeconds(20));
        var managerTask = CheckOneAsync(manager, token, deadline ?? TimeSpan.FromSeconds(20));
        await Task.WhenAll(dllTask, managerTask);
        return (await dllTask, await managerTask);
    }

    private static async Task<UpdateCheckResult<T>> CheckOneAsync<T>(
        Func<CancellationToken, Task<T>> check, CancellationToken token, TimeSpan deadline)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(deadline);
        try { return new(await check(timeout.Token), null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new(default, new TimeoutException("GitHub update check timed out. Retry when the connection is available."));
        }
        catch (Exception error) { return new(default, error); }
    }
}
