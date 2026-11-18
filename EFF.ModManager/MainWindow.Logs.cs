using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace EffModManager;

public partial class MainWindow
{
    private readonly ObservableCollection<ActivityEntry> _logEntries = [];

    private void InitializeLogs()
    {
        ConfigureLogLocation();
        foreach (var entry in ActivityLog.Default.Snapshot().Reverse()) _logEntries.Add(entry);
        LogList.ItemsSource = _logEntries;
        ActivityLog.Default.Added += LogAdded;
        LanguageService.Instance.PropertyChanged += LogLanguageChanged;
        Closed += (_, _) =>
        {
            ActivityLog.Default.Added -= LogAdded;
            LanguageService.Instance.PropertyChanged -= LogLanguageChanged;
        };
        ActivityLog.Default.Info("log.application", "EFF Mod Manager v" + ManagerUpdateService.CurrentVersion.ToString(3));
        RefreshLogLocation();
    }

    private void ConfigureLogLocation()
    {
        var root = SharedResources.ResourceRoot;
        ActivityLog.Default.Configure(string.IsNullOrWhiteSpace(root)
            ? Path.Combine(AppContext.BaseDirectory, "logs") : Path.Combine(root, ".effmm-cache", "logs"));
    }

    private void RefreshLogLocation() => LogLocation.Text = ActivityLog.Default.FilePath +
        (ActivityLog.Default.PersistenceError.Length == 0 ? "" : "\n" + T("log.saveFailed") + ActivityLog.Default.PersistenceError);

    private void LogAdded(ActivityEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => LogAdded(entry));
            return;
        }
        _logEntries.Insert(0, entry);
        while (_logEntries.Count > 400) _logEntries.RemoveAt(_logEntries.Count - 1);
        if (entry.Level is "error" or "warning") LogList.SelectedItem = entry;
        RefreshLogLocation();
    }

    private void LogLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        LogList.Items.Refresh();
        ShowLogDetails();
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowLogDetails();

    private void ShowLogDetails()
    {
        if (LogMessage is null) return;
        var entry = LogList.SelectedItem as ActivityEntry;
        LogMessage.Text = entry?.Message ?? T("log.select");
        LogRecovery.Text = entry?.Steps ?? "";
        LogLinks.ItemsSource = entry?.Recovery?.Links.Distinct().ToArray();
        LogDetails.Text = entry?.Details ?? "";
        LogDestinationButton.IsEnabled = entry?.Recovery?.Folder is { Length: > 0 };
    }

    private void OpenLogLink(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { ActivityLog.Default.Failure("log.openLink", error); }
        e.Handled = true;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = ReferenceEquals(sender, LogDestinationButton)
            ? (LogList.SelectedItem as ActivityEntry)?.Recovery?.Folder : Path.GetDirectoryName(ActivityLog.Default.FilePath);
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { folder } });
        }
        catch (Exception error) { ActivityLog.Default.Failure("log.openFolderError", error); }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not ActivityEntry entry) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, new[]
            {
                entry.Time.ToString("O") + " " + entry.OperationText + " · " + entry.Summary,
                entry.Message, entry.Steps, string.Join(Environment.NewLine, entry.Recovery?.Links ?? []), entry.Details
            }.Where(s => s.Length > 0)));
        }
        catch (Exception error) { ActivityLog.Default.Failure("log.copy", error); }
    }
}
