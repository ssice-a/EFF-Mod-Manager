using System.Diagnostics;
using System.IO.Compression;

namespace EffModManager;

internal static class ManagerUpdateService
{
    internal static Version CurrentVersion => typeof(ManagerUpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0);

    internal static async Task<(GitHubRelease? Release, GitHubAsset? Asset, Version? Version)> CheckAsync(CancellationToken token)
    {
        var release = await GitHubReleaseService.GetLatestManagerAsync(token);
        if (release is null) return (null, null, null);
        var asset = release.assets
            .Where(item => item.name.StartsWith("EFF-Mod-Manager-v", StringComparison.OrdinalIgnoreCase) && item.name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => AssetVersion(item.name))
            .FirstOrDefault();
        return asset is null ? (release, null, null) : (release, asset, AssetVersion(asset.name));
    }

    internal static async Task UpdateAndRestartAsync(GitHubAsset asset, Version version, IProgress<string>? progress, CancellationToken token)
    {
        var processPath = Environment.ProcessPath ?? throw new IOException("Could not locate the running app.");
        var installRoot = Path.GetDirectoryName(processPath)!;
        var stage = Path.Combine(SettingsStore.DataRoot, "staging", "manager-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var package = Path.Combine(stage, asset.name);
            await GitHubReleaseService.DownloadVerifiedAsync(asset, package, progress, token);
            var unpacked = Path.Combine(stage, "unpacked");
            await ExtractAsync(package, unpacked, token);
            var executableName = Path.GetFileName(processPath);
            var updatedExe = Path.Combine(unpacked, executableName);
            if (!File.Exists(updatedExe)) throw new InvalidDataException("Manager update does not contain " + executableName);
            var script = Path.Combine(stage, "apply-update.ps1");
            File.WriteAllText(script, BuildScript(stage, unpacked, installRoot, executableName, Environment.ProcessId), new System.Text.UTF8Encoding(false));
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = stage
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            using var updater = Process.Start(start);
            if (updater is null) throw new IOException("Could not start the update helper.");
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            throw;
        }
    }

    private static string BuildScript(string stage, string unpacked, string installRoot, string executable, int processId)
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var stagedExe = Path.Combine(installRoot, executable + ".effmm-update");
        return string.Join("\n", [
            "$ErrorActionPreference='Stop'",
            $"$pidToWait={processId}",
            "try { Wait-Process -Id $pidToWait -Timeout 180 } catch {}",
            $"$stage={Q(stage)}",
            $"$source={Q(unpacked)}",
            $"$target={Q(installRoot)}",
            $"$exe={Q(executable)}",
            $"$staged={Q(stagedExe)}",
            "$exePath=Join-Path $source $exe",
            "Get-ChildItem -LiteralPath $source -Force -File -Recurse | Where-Object { $_.FullName -ne $exePath } | ForEach-Object { $relative=[System.IO.Path]::GetRelativePath($source,$_.FullName); $destination=Join-Path $target $relative; $parent=Split-Path -Parent $destination; New-Item -ItemType Directory -Path $parent -Force | Out-Null; Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }",
            "Copy-Item -LiteralPath $exePath -Destination $staged -Force",
            "Move-Item -LiteralPath $staged -Destination (Join-Path $target $exe) -Force",
            "Start-Process -FilePath (Join-Path $target $exe)",
            "Set-Location $env:TEMP",
            "Remove-Item -LiteralPath $stage -Recurse -Force",
            ""
        ]);
    }

    private static async Task ExtractAsync(string package, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count > 100) throw new InvalidDataException("Manager update contains too many files.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.EndsWith('/')) continue;
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("Manager update contains a symbolic link.");
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || relative.StartsWith('/') || relative.Contains(':') || parts.Any(part => part is "." or ".."))
                throw new InvalidDataException("Manager update contains an unsafe path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 200L * 1024 * 1024) throw new InvalidDataException("Manager update exceeds the extraction limit.");
            var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(parts)));
            var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manager update escapes its installation folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, token);
        }
    }

    private static Version AssetVersion(string name)
    {
        var marker = "EFF-Mod-Manager-v";
        var start = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return new Version(0, 0);
        var value = name[(start + marker.Length)..].Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }
}
