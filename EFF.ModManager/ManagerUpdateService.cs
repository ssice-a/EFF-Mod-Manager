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
        EnsureInstallRootWritable(installRoot);
        if (Path.GetFileName(asset.name) != asset.name || string.IsNullOrWhiteSpace(asset.name))
            throw new InvalidDataException("Manager update asset name is invalid.");

        var cacheRoot = Path.Combine(SettingsStore.DataRoot, "update-cache", "manager");
        var package = Path.Combine(cacheRoot, asset.name);
        Directory.CreateDirectory(cacheRoot);
        if (await GitHubReleaseService.IsVerifiedAssetFileAsync(asset, package, token))
        {
            progress?.Report("Using verified cached Manager update package.");
        }
        else
        {
            if (File.Exists(package)) File.Delete(package);
            await GitHubReleaseService.DownloadVerifiedAsync(asset, package, progress, token);
        }

        var stage = Path.Combine(SettingsStore.DataRoot, "staging", "manager-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var handedOff = false;
        try
        {
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
            handedOff = true;
        }
        catch
        {
            if (!handedOff && Directory.Exists(stage)) Directory.Delete(stage, true);
            throw;
        }
    }

    internal static void EnsureInstallRootWritable(string installRoot)
    {
        if (!Directory.Exists(installRoot)) throw new IOException("Manager installation folder does not exist: " + installRoot);
        var probe = Path.Combine(installRoot, ".effmm-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
                stream.Flush(true);
            }
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    internal static string BuildScript(string stage, string unpacked, string installRoot, string executable, int processId, bool showFailureUi = true)
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        return string.Join("\n", [
            "$ErrorActionPreference='Stop'",
            $"$pidToWait={processId}",
            $"$stage={Q(stage)}",
            $"$source={Q(unpacked)}",
            $"$target={Q(installRoot)}",
            $"$exe={Q(executable)}",
            $"$showFailureUi={(showFailureUi ? "$true" : "$false")}",
            "$log=Join-Path $stage 'update.log'",
            "$rollback=Join-Path $stage 'rollback'",
            "$changed=New-Object System.Collections.Generic.List[object]",
            "function Write-UpdateLog([string]$message) { try { Add-Content -LiteralPath $log -Value ((Get-Date -Format o) + ' ' + $message) } catch {} }",
            "$success=$false",
            "try {",
            "  if ($pidToWait -gt 0) {",
            "    if (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {",
            "      Wait-Process -Id $pidToWait -Timeout 180 -ErrorAction SilentlyContinue",
            "      if (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { throw 'Timed out waiting for the old Manager process to exit.' }",
            "    }",
            "  }",
            "  $exePath=Join-Path $source $exe",
            "  if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Update archive is missing the Manager executable.' }",
            "  $files=Get-ChildItem -LiteralPath $source -Force -File -Recurse | Where-Object { $_.FullName -ne $exePath }",
            "  foreach ($file in $files) {",
            "    $relative=[System.IO.Path]::GetRelativePath($source,$file.FullName)",
            "    $destination=Join-Path $target $relative",
            "    $backup=Join-Path $rollback $relative",
            "    $parent=Split-Path -Parent $destination",
            "    New-Item -ItemType Directory -Path $parent -Force | Out-Null",
            "    $hadOriginal=Test-Path -LiteralPath $destination -PathType Leaf",
            "    $changed.Add([pscustomobject]@{Path=$destination;Backup=$backup;HadOriginal=$hadOriginal})",
            "    if ($hadOriginal) { New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null; Copy-Item -LiteralPath $destination -Destination $backup -Force }",
            "    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force",
            "  }",
            "  $exeDestination=Join-Path $target $exe",
            "  $exeBackup=Join-Path $rollback $exe",
            "  $hadExe=Test-Path -LiteralPath $exeDestination -PathType Leaf",
            "  $changed.Add([pscustomobject]@{Path=$exeDestination;Backup=$exeBackup;HadOriginal=$hadExe})",
            "  if ($hadExe) { Copy-Item -LiteralPath $exeDestination -Destination $exeBackup -Force }",
            "  $temporaryExe=$exeDestination + '.effmm-update'",
            "  Copy-Item -LiteralPath $exePath -Destination $temporaryExe -Force",
            "  Move-Item -LiteralPath $temporaryExe -Destination $exeDestination -Force",
            "  $newProcess=Start-Process -FilePath $exeDestination -PassThru",
            "  Start-Sleep -Seconds 5",
            "  $newProcess.Refresh()",
            "  if ($newProcess.HasExited) { throw 'Updated Manager exited during startup.' }",
            "  Write-UpdateLog 'Manager update succeeded.'",
            "  $success=$true",
            "} catch {",
            "  $failure=$_.Exception.Message",
            "  Write-UpdateLog ('Manager update failed: ' + $failure)",
            "  for ($i=$changed.Count-1; $i -ge 0; $i--) {",
            "    $item=$changed[$i]",
            "    try {",
            "      if ($item.HadOriginal) { Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Force }",
            "      elseif (Test-Path -LiteralPath $item.Path) { Remove-Item -LiteralPath $item.Path -Force }",
            "    } catch { Write-UpdateLog ('Rollback failed for ' + $item.Path + ': ' + $_.Exception.Message) }",
            "  }",
            "  if ($showFailureUi) {",
            "    try { if (-not (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue)) { Start-Process -FilePath (Join-Path $target $exe) } } catch { Write-UpdateLog ('Could not restart previous Manager: ' + $_.Exception.Message) }",
            "    try { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show(('Manager update failed. The previous version was restored when possible.' + [Environment]::NewLine + [Environment]::NewLine + $failure + [Environment]::NewLine + [Environment]::NewLine + 'Log: ' + $log), 'EFF Mod Manager update failed', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null } catch {}",
            "  }",
            "  exit 1",
            "}",
            "if ($success) { try { Set-Location $env:TEMP; Remove-Item -LiteralPath $stage -Recurse -Force } catch {} }",
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
