using System.Net;
using EffModManager;

internal static class UpdateCheckTests
{
    internal static async Task RunAsync()
    {
        var release = new GitHubRelease { tag_name = "v9.0.0" };
        var result = await UpdateCheckService.CheckAsync(
            _ => Task.FromException<Version?>(new HttpRequestException("DLL API failed")),
            _ => Task.FromResult<(GitHubRelease?, GitHubAsset?, Version?)>((release, null, new Version(9, 0, 0))),
            CancellationToken.None);
        Require(!result.Dll.Succeeded && result.Manager.Succeeded && result.Manager.Value.Release == release,
            "A failed DLL query blocked the Manager query.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result = await UpdateCheckService.CheckAsync(
            async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return null; },
            async _ => { await entered.Task; return (release, null, new Version(9, 0, 0)); },
            CancellationToken.None, TimeSpan.FromMilliseconds(40));
        Require(result.Dll.Error is TimeoutException && result.Manager.Succeeded,
            "One component's timeout prevented the other component's successful result.");
        result = await UpdateCheckService.CheckAsync(_ => Task.FromResult<Version?>(new Version(2, 0, 0)),
            _ => Task.FromException<(GitHubRelease?, GitHubAsset?, Version?)>(new IOException("Manager failed")),
            CancellationToken.None);
        Require(result.Dll.Succeeded && !result.Manager.Succeeded, "A failed Manager query discarded the DLL result.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await UpdateCheckService.CheckAsync(token => Task.FromCanceled<Version?>(token),
                    token => Task.FromCanceled<(GitHubRelease?, GitHubAsset?, Version?)>(token), cancelled.Token);
                throw new Exception("Shutdown cancellation was swallowed.");
            }
            catch (OperationCanceledException) { }
        }

        var rateLimit = new HttpResponseMessage(HttpStatusCode.Forbidden);
        rateLimit.Headers.Add("X-RateLimit-Remaining", "0");
        using (var handler = new SequenceHandler(rateLimit))
        using (var client = new HttpClient(handler))
        {
            try
            {
                await GitHubReleaseService.GetLatestAsync(client, "ssice-a/EFF", CancellationToken.None,
                    (_, _) => throw new Exception("403 must not be retried."));
                throw new Exception("Rate limiting was treated as a successful update check.");
            }
            catch (HttpRequestException error)
            {
                Require(error.StatusCode == HttpStatusCode.Forbidden && error.Message.Contains("rate limit") && handler.Calls == 1,
                    "Rate-limit failure lost its reason or retried immediately.");
            }
        }
        using (var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), Success()))
        using (var client = new HttpClient(handler))
        {
            var waits = 0;
            var found = await GitHubReleaseService.GetLatestAsync(client, "ssice-a/EFF", CancellationToken.None,
                (_, _) => { waits++; return Task.CompletedTask; });
            Require(found?.tag_name == "v9.0.0" && handler.Calls == 2 && waits == 1, "Transient 503 did not recover.");
        }
        var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        tooMany.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
        using (var handler = new SequenceHandler(tooMany))
        using (var client = new HttpClient(handler))
        {
            try
            {
                await GitHubReleaseService.GetLatestAsync(client, "ssice-a/EFF", CancellationToken.None,
                    (_, _) => throw new Exception("A long Retry-After must not block the UI."));
                throw new Exception("429 was treated as a successful check.");
            }
            catch (HttpRequestException error) { Require(error.Message.Contains("rate limit"), "429 reason missing."); }
        }
        using (var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.NotFound)))
        using (var client = new HttpClient(handler))
            Require(await GitHubReleaseService.GetLatestAsync(client, "ssice-a/EFF", CancellationToken.None) is null,
                "A genuinely missing Release was not preserved.");
        Console.WriteLine("PASS update checks isolate component failure/timeouts; GitHub rate limits and bounded transient retries");
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"tag_name\":\"v9.0.0\",\"assets\":[]}")
    };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(responses[Calls++]);
        }
    }
}
