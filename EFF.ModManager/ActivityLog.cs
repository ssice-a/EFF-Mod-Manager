using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EffModManager;

internal sealed record RecoveryAdvice(string StepsKey, string Destination, string Folder, string[] Links, string Workspace = "");

internal sealed record ActivityEntry(DateTimeOffset Time, string Level, string Operation, string Reason,
    string Message, string Details, RecoveryAdvice? Recovery)
{
    [JsonIgnore]
    public string Timestamp => Time.ToLocalTime().ToString("MM-dd HH:mm:ss");
    [JsonIgnore]
    public string LevelText => LanguageService.Instance["log." + Level];
    [JsonIgnore]
    public string OperationText => LanguageService.Instance[Operation];
    [JsonIgnore]
    public string ReasonText => LanguageService.Instance[Reason];
    [JsonIgnore]
    public string Summary => Reason.Length == 0 ? Message : ReasonText + " " + Message;
    [JsonIgnore]
    public string Steps => Recovery is null ? "" : string.Format(LanguageService.Instance[Recovery.StepsKey], Recovery.Destination) +
        (Recovery.Workspace.Length == 0 ? "" : "\n" + string.Format(LanguageService.Instance["log.indexWorkspace"], Recovery.Workspace));
}

// Operation boundaries record failures; expected probe misses and byte progress do not flood this log.
// Keep both the UI and disk bounded, including across restarts.
internal sealed class ActivityLog
{
    internal static ActivityLog Default { get; } = new();
    private const int Capacity = 400;
    private const long FileLimit = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly List<ActivityEntry> _entries = [];
    internal event Action<ActivityEntry>? Added;
    internal string FilePath { get; private set; } = "";
    internal string PersistenceError { get; private set; } = "";
    internal ActivityEntry[] Snapshot() { lock (_gate) return _entries.ToArray(); }

    internal void Configure(string folder)
    {
        lock (_gate)
        {
            var path = Path.Combine(Path.GetFullPath(folder), "manager.jsonl");
            if (path == FilePath) return;
            FilePath = path;
            PersistenceError = "";
            // Reconfiguration keeps this session; startup also recovers bounded history.
            if (_entries.Count != 0) return;
            try
            {
                foreach (var file in new[] { path + ".previous", path }.Where(File.Exists))
                {
                    // Do not read arbitrary oversized files into memory.
                    if (new FileInfo(file).Length > FileLimit * 2) continue;
                    foreach (var line in File.ReadLines(file))
                    {
                        try
                        {
                            var entry = JsonSerializer.Deserialize<ActivityEntry>(line);
                            if (entry is not null) { _entries.Add(entry); Trim(); }
                        }
                        catch (JsonException) { /* A partial final line must not hide earlier events. */ }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { PersistenceError = error.Message; }
        }
    }

    internal static string Classify(Exception error)
    {
        if (error is OperationCanceledException or TimeoutException || error.InnerException is TimeoutException) return "log.timeout";
        if (error is HttpRequestException request)
            return request.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? "log.rateLimit" : "log.network";
        if (error is FileNotFoundException or DirectoryNotFoundException) return "log.missing";
        if (error is UnauthorizedAccessException) return "log.access";
        return "log.failed";
    }

    internal void Failure(string operation, Exception error, RecoveryAdvice? advice = null, bool warning = false)
    {
        var contextual = error.Data[nameof(RecoveryAdvice)] as RecoveryAdvice;
        if (contextual is not null && contextual.Destination.Length > 0) advice = contextual;
        else advice ??= contextual;
        Write(new(DateTimeOffset.Now, warning ? "warning" : "error", operation, Classify(error), error.Message, error.ToString(), advice));
    }

    internal static T WithRecovery<T>(T error, RecoveryAdvice advice) where T : Exception
    {
        error.Data[nameof(RecoveryAdvice)] = advice;
        return error;
    }

    internal void Info(string operation, string message) => Write(new(DateTimeOffset.Now, "info", operation, "", message, "", null));

    private void Trim() { if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity); }

    private void Write(ActivityEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Trim();
            if (FilePath.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= FileLimit)
                        File.Move(FilePath, FilePath + ".previous", true);
                    File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
                    PersistenceError = "";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { PersistenceError = error.Message; }
            }
        }
        Added?.Invoke(entry);
    }
}

internal static class RecoveryHelp
{
    internal const string EffReleases = "https://github.com/ssice-a/EFF/releases";
    internal const string EffIndexCatalog = "https://github.com/ssice-a/EFF/releases/latest/download/eff-index-catalog.json";
    internal const string EffIndexRequest = "https://github.com/ssice-a/EFF/issues/new?title=Publish%20game-matched%20asset%20index";
    internal const string ManagerReleases = "https://github.com/ssice-a/EFF-Mod-Manager/releases/latest";
    internal const string ToolsManifest = "https://github.com/ssice-a/EFF/releases/latest/download/eff-native-tools.json";

    internal static RecoveryAdvice Index(string root, string workspace = "", string? url = null)
    {
        var folder = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "indexes");
        return new(folder.Length == 0 ? "log.indexChooseRoot" : "log.indexSteps",
            folder, folder, url is null ? [EffIndexCatalog, EffReleases, EffIndexRequest] : [url, EffIndexCatalog, EffReleases, EffIndexRequest], workspace);
    }

    internal static RecoveryAdvice Tools(string root, string? url = null)
    {
        var folder = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "tools", "downloads");
        return new(folder.Length == 0 ? "log.toolsChooseRoot" : "log.toolsSteps", folder, folder,
            url is null ? [ToolsManifest, EffReleases] : [url, ToolsManifest, EffReleases]);
    }

    internal static RecoveryAdvice Release(string repository, string destination = "", string? url = null)
        => new(repository == "ssice-a/EFF-Mod-Manager" ? "log.managerSteps" : "log.dllSteps", destination,
            Directory.Exists(destination) ? destination : Path.GetDirectoryName(destination) ?? "",
            url is null ? ["https://github.com/" + repository + "/releases/latest"] : [url, "https://github.com/" + repository + "/releases/latest"]);
}
