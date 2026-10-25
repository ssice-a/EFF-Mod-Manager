using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DragEventArgs = System.Windows.DragEventArgs;

namespace EffModManager;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _updateCheckGate = new(1, 1);
    private List<ModEntry> _allMods = [];
    private bool _initializing = true;
    private bool _busy;
    private bool _repairSelectionSweepActive;
    private bool _repairSelectionSweepValue;
    private int _repairSelectionSweepLastIndex = -1;
    private Version? _latestDll;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        LanguageService.Instance.IsEnglish = _settings.English;
        ChineseRadio.IsChecked = !_settings.English;
        EnglishRadio.IsChecked = _settings.English;
        GamePathBox.Text = _settings.GamePath;
        ResourceRootBox.Text = _settings.ResourceRoot;
        SettingsResourceRootBox.Text = _settings.ResourceRoot;
        ManagerVersionText.Text = "v" + ManagerUpdateService.CurrentVersion.ToString(3);
        _initializing = false;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => _shutdown.Cancel();
        RefreshMods();
        RefreshDllState();
        FooterGame.Text = _settings.GamePath;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var savedGamePath = _settings.GamePath;
            var matches = await Task.Run(() => GameDiscovery.Find(savedGamePath), _shutdown.Token);
            if (GameDiscovery.IsGame(_settings.GamePath)) SetGamePath(Path.GetFullPath(_settings.GamePath));
            else if (matches.Count == 1) SetGamePath(matches[0]);
            else if (matches.Count > 1) SetStatus(string.Format(T("status.multipleGames"), matches.Count));
            else SetStatus(T("status.gameMissing"));
            RefreshMods();
            RefreshDllState();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { SetStatus(error.Message); }

        _ = CheckUpdatesAsync(automatic: true);
    }

    private string T(string key) => LanguageService.Instance[key];
    private string GamePath => Path.GetFullPath(GamePathBox.Text.Trim().Trim('"'));
    private bool HasGame => GameDiscovery.IsGame(GamePathBox.Text.Trim().Trim('"'));

    private void SetStatus(string value)
    {
        FooterStatus.Text = value;
        HeaderStatus.Text = value;
    }

    private void SetGamePath(string path)
    {
        if (!string.Equals(_settings.GamePath, path, StringComparison.OrdinalIgnoreCase))
            _settings.IgnoredDllVersion = "";
        GamePathBox.Text = path;
        _settings.GamePath = path;
        FooterGame.Text = path;
        SettingsStore.Save(_settings);
        RefreshDllState();
        RefreshMods();
    }

    private async Task ExecuteAsync(Func<Task> action, bool showError = true)
    {
        if (_busy) return;
        _busy = true;
        MainTabs.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try { await action(); }
        catch (OperationCanceledException) { SetStatus(T("footer.ready")); }
        catch (Exception error)
        {
            SetStatus(error.Message);
            if (MainTabs.SelectedIndex == 3) RepairStatusText.Text = error.Message;
            if (showError) MessageBox.Show(this, error.Message, T("dialog.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            MainTabs.IsEnabled = true;
            _busy = false;
        }
    }

    private void GamePath_TextChanged(object sender, TextChangedEventArgs e)
    {
        var path = GamePathBox.Text.Trim().Trim('"');
        var valid = GameDiscovery.IsGame(path);
        InstallDllButton.IsEnabled = valid;
        if (valid) RefreshDllState();
    }

    private void GamePath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (GameDiscovery.IsGame(GamePathBox.Text.Trim().Trim('"')))
            SetGamePath(Path.GetFullPath(GamePathBox.Text.Trim().Trim('"')));
    }

    private void ResourceRoot_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var text = sender is TextBox box ? box.Text.Trim() : ResourceRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        _settings.ResourceRoot = text;
        ResourceRootBox.Text = text;
        SettingsResourceRootBox.Text = text;
        SettingsStore.Save(_settings);
    }

    private void SettingsResourceRoot_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || ResourceRootBox is null) return;
        ResourceRootBox.Text = SettingsResourceRootBox.Text;
    }

    private async void ScanGame_Click(object sender, RoutedEventArgs e)
    {
        var savedGamePath = GamePathBox.Text.Trim().Trim('"');
        await ExecuteAsync(async () =>
        {
            var matches = await Task.Run(() => GameDiscovery.Find(savedGamePath), _shutdown.Token);
            if (matches.Count == 0) { SetStatus(T("status.gameMissing")); return; }
            if (matches.Count == 1) { SetGamePath(matches[0]); SetStatus(T("status.gameFound")); return; }
            var dialog = new OpenFolderDialog { Title = T("game.title"), InitialDirectory = GameDiscovery.IsGame(GamePathBox.Text) ? GamePathBox.Text : matches[0] };
            if (dialog.ShowDialog(this) == true && GameDiscovery.IsGame(dialog.FolderName)) SetGamePath(dialog.FolderName);
        });
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("game.title"), InitialDirectory = Directory.Exists(GamePathBox.Text) ? GamePathBox.Text : "" };
        if (dialog.ShowDialog(this) != true) return;
        if (!GameDiscovery.IsGame(dialog.FolderName))
        {
            MessageBox.Show(this, T("status.gameMissing"), T("dialog.title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetGamePath(dialog.FolderName);
        SetStatus(T("status.gameFound"));
    }

    private void RefreshDllState()
    {
        if (!HasGame)
        {
            DllStateText.Text = T("dll.missing");
            DllVersionText.Text = "";
            LatestDllText.Text = _latestDll?.ToString() ?? "—";
            InstallDllButton.IsEnabled = false;
            UninstallDllButton.IsEnabled = false;
            return;
        }

        var state = DllInstallService.ReadState(GamePath);
        var installedVersion = DllInstallService.InstalledVersion(GamePath);
        var dllFile = Path.Combine(GamePath, "plugin", "eff.dll");
        InstallDllButton.IsEnabled = true;
        if (state is { Files.Count: > 0 })
        {
            DllStateText.Text = T("dll.installed");
            var installedText = "EFF v" + (installedVersion?.ToString() ?? state.Version);
            if (DateTimeOffset.TryParse(state.InstalledAt, out var installedAt)) installedText += " · " + installedAt.ToLocalTime().ToString("yyyy-MM-dd");
            DllVersionText.Text = installedText;
            UninstallDllButton.IsEnabled = true;
        }
        else if (File.Exists(dllFile))
        {
            DllStateText.Text = T("dll.unknown");
            DllVersionText.Text = installedVersion is null ? dllFile : "EFF v" + installedVersion;
            UninstallDllButton.IsEnabled = false;
        }
        else
        {
            DllStateText.Text = T("dll.missing");
            DllVersionText.Text = "";
            UninstallDllButton.IsEnabled = false;
        }

        LatestDllText.Text = _latestDll?.ToString() ?? "—";
        if (_latestDll is not null && installedVersion is not null && _latestDll > installedVersion)
            DllVersionText.Text += "   ·   " + T("status.updateAvailable");
    }

    private async void InstallDll_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(InstallLatestDllAsync);

    private Task InstallLatestDllAsync() => InstallLatestDllAsync(ignoreDeclinedUnmanaged: false);

    private async Task InstallLatestDllAsync(bool ignoreDeclinedUnmanaged)
    {
        if (!EnsureGame()) return;
        if (File.Exists(Path.Combine(GamePath, "plugin", "eff.dll")) && !DllInstallService.IsManaged(GamePath))
        {
            var result = MessageBox.Show(this, T("dialog.unmanagedDll"), T("dialog.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                if (ignoreDeclinedUnmanaged && _latestDll is not null)
                {
                    _settings.IgnoredDllVersion = _latestDll.ToString();
                    SettingsStore.Save(_settings);
                }
                return;
            }
        }
        var progress = new Progress<string>(SetStatus);
        var version = await DllInstallService.InstallOrUpdateAsync(GamePath, progress, _shutdown.Token);
        _settings.IgnoredDllVersion = "";
        SettingsStore.Save(_settings);
        SetStatus(T("status.dllInstalled") + " · v" + version);
        RefreshDllState();
    }

    private async void UninstallDll_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(() =>
        {
            if (!EnsureGame()) return Task.CompletedTask;
            var result = MessageBox.Show(this, T("dialog.uninstallDll"), T("dialog.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return Task.CompletedTask;
            var outcome = DllInstallService.Uninstall(GamePath);
            _settings.IgnoredDllVersion = "";
            SettingsStore.Save(_settings);
            RefreshDllState();
            SetStatus(T("status.dllRemoved") + (outcome == "Removed" ? "" : " · " + outcome));
            return Task.CompletedTask;
        });
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(automatic: false);

    private async Task CheckUpdatesAsync(bool automatic)
    {
        if (!await _updateCheckGate.WaitAsync(0)) return;
        try
        {
            SetStatus(T("footer.checking"));
            var updateTask = Task.Run(async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var dll = await DllInstallService.GetLatestVersionAsync(timeout.Token);
                var manager = await ManagerUpdateService.CheckAsync(timeout.Token);
                return (Dll: dll, Manager: manager);
            }, _shutdown.Token);

            (Version? Dll, (GitHubRelease? Release, GitHubAsset? Asset, Version? Version) Manager) result;
            try { result = await updateTask; }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
            {
                SetStatus(T("status.updateCheckFailed"));
                ManagerVersionText.Text = T("status.updateCheckFailed");
                return;
            }
            catch (Exception error)
            {
                _latestDll = null;
                RefreshDllState();
                SetStatus(T("status.updateCheckFailed"));
                ManagerVersionText.Text = automatic ? T("status.updateCheckFailed") : error.Message;
                return;
            }

            _latestDll = result.Dll;
            RefreshDllState();
            if (result.Manager.Release is null)
            {
                ManagerVersionText.Text = "v" + ManagerUpdateService.CurrentVersion.ToString(3) + " · " + T("status.managerReleaseMissing");
            }
            else if (result.Manager.Version is null || result.Manager.Asset is null)
            {
                ManagerVersionText.Text = "v" + ManagerUpdateService.CurrentVersion.ToString(3);
            }
            else if (result.Manager.Version > ManagerUpdateService.CurrentVersion)
            {
                ManagerVersionText.Text = "v" + ManagerUpdateService.CurrentVersion.ToString(3) + " → v" + result.Manager.Version;
                if (!automatic || !string.Equals(_settings.IgnoredManagerVersion, result.Manager.Version.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var choice = MessageBox.Show(this,
                        string.Format(T("dialog.managerUpdate"), result.Manager.Version),
                        T("dialog.title"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (choice == MessageBoxResult.Yes)
                    {
                        SetStatus(T("status.managerUpdating"));
                        var progress = new Progress<string>(SetStatus);
                        await ManagerUpdateService.UpdateAndRestartAsync(result.Manager.Asset, result.Manager.Version, progress, _shutdown.Token);
                        Close();
                        return;
                    }
                    _settings.IgnoredManagerVersion = result.Manager.Version.ToString();
                    SettingsStore.Save(_settings);
                }
            }
            else ManagerVersionText.Text = "v" + ManagerUpdateService.CurrentVersion.ToString(3) + " · " + T("status.upToDate");

            var dllPath = HasGame ? Path.Combine(GamePath, "plugin", "eff.dll") : "";
            var hasInstalledDll = dllPath.Length > 0 && File.Exists(dllPath);
            var installedDll = hasInstalledDll ? DllInstallService.InstalledVersion(GamePath) : null;
            var dllUpdateAvailable = hasInstalledDll && _latestDll is not null && (installedDll is null || installedDll < _latestDll);
            if (dllUpdateAvailable && _latestDll is not null)
            {
                var updateVersion = _latestDll;
                if (!automatic || !string.Equals(_settings.IgnoredDllVersion, updateVersion.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var currentVersion = installedDll?.ToString() ?? T("status.versionUnknown");
                    var choice = MessageBox.Show(this,
                        string.Format(T("dialog.dllUpdate"), currentVersion, updateVersion),
                        T("dialog.title"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (choice == MessageBoxResult.Yes)
                    {
                        await ExecuteAsync(() => InstallLatestDllAsync(ignoreDeclinedUnmanaged: true));
                        return;
                    }
                    _settings.IgnoredDllVersion = updateVersion.ToString();
                    SettingsStore.Save(_settings);
                }
                SetStatus(T("status.updateAvailable") + " · EFF v" + updateVersion);
            }
            else
            {
                if (_latestDll is not null && installedDll is not null && installedDll >= _latestDll && _settings.IgnoredDllVersion.Length > 0)
                {
                    _settings.IgnoredDllVersion = "";
                    SettingsStore.Save(_settings);
                }
                SetStatus(T("status.updatesChecked"));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            SetStatus(T("status.updateCheckFailed"));
            ManagerVersionText.Text = error.Message;
        }
        finally { _updateCheckGate.Release(); }
    }

    private void RefreshMods(string? selectedFolder = null)
    {
        if (!HasGame)
        {
            _allMods = [];
            ModsList.ItemsSource = null;
            RepairModsList.ItemsSource = null;
            ModCountText.Text = "0";
            EnabledCountText.Text = "0";
            ModListCount.Text = string.Format(T("mods.count"), 0);
            EmptyModsOverlay.Visibility = Visibility.Visible;
            RepairEmptyText.Visibility = Visibility.Visible;
            ClearDetails();
            UpdateRepairSelectionToggleText();
            return;
        }

        _allMods = ModService.Scan(GamePath);
        ModCountText.Text = _allMods.Count.ToString();
        EnabledCountText.Text = _allMods.Count(mod => mod.IsEnabled).ToString();
        var selected = _allMods.FirstOrDefault(mod => string.Equals(mod.Folder, selectedFolder, StringComparison.OrdinalIgnoreCase))
            ?? ModsList.SelectedItem as ModEntry;
        ApplyModFilter(selected?.Folder);
        foreach (var mod in _allMods) mod.IsRepairSelected = true;
        RepairModsList.ItemsSource = new ObservableCollection<ModEntry>(_allMods);
        RepairEmptyText.Visibility = _allMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RepairCountText.Text = string.Format(T("repair.modCount"), _allMods.Count);
        UpdateRepairSelectionToggleText();
        if (selected is null && _allMods.Count > 0) ModsList.SelectedIndex = 0;
        else if (_allMods.Count == 0) ClearDetails();
    }

    private void ApplyModFilter(string? selectedFolder = null)
    {
        var query = ModSearchBox.Text.Trim();
        var filtered = _allMods.Where(mod => string.IsNullOrWhiteSpace(query) ||
            mod.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            mod.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            mod.Id.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ModsList.ItemsSource = filtered;
        ModListCount.Text = string.Format(T("mods.count"), filtered.Count);
        EmptyModsOverlay.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = filtered.FirstOrDefault(mod => string.Equals(mod.Folder, selectedFolder, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) ModsList.SelectedItem = selected;
        else if (filtered.Count > 0 && ModsList.SelectedItem is null) ModsList.SelectedIndex = 0;
        else if (filtered.Count == 0) ClearDetails();
    }

    private void ModSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing) ApplyModFilter(ModsList.SelectedItem is ModEntry mod ? mod.Folder : null);
    }

    private void ModsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModsList.SelectedItem is not ModEntry mod) { ClearDetails(); return; }
        ShowDetails(mod);
    }

    private void ShowDetails(ModEntry mod)
    {
        DetailsName.Text = mod.Name;
        DetailsMeta.Text = string.Join("  ·  ", new[] { mod.Author, mod.Version, mod.IsEnabled ? T("mods.enabled") : T("mods.disabled") }.Where(value => !string.IsNullOrWhiteSpace(value)));
        DetailsDescription.Text = string.IsNullOrWhiteSpace(mod.Description) ? T("mods.unknown") : mod.Description;
        DetailsKeys.ItemsSource = mod.Hotkeys;
        DetailsPath.Text = mod.Folder;
    }

    private void ClearDetails()
    {
        DetailsName.Text = "";
        DetailsMeta.Text = "";
        DetailsDescription.Text = "";
        DetailsKeys.ItemsSource = null;
        DetailsPath.Text = "";
    }

    private async void ModEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkbox || checkbox.DataContext is not ModEntry mod) return;
        var enabled = checkbox.IsChecked == true;
        await ExecuteAsync(() =>
        {
            ModService.SetEnabled(mod, enabled);
            RefreshMods(mod.Folder);
            SetStatus(T("status.modToggled") + ": " + mod.Name);
            return Task.CompletedTask;
        });
    }

    private async void ImportMod_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGame()) return;
        var dialog = new OpenFileDialog { Filter = "Mod packages (*.zip)|*.zip|All files (*.*)|*.*", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await ImportPathsAsync(dialog.FileNames);
    }

    private async void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (ModsList.SelectedItem is not ModEntry mod) return;
        if (MessageBox.Show(this, T("dialog.confirmDelete"), T("dialog.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ExecuteAsync(() =>
        {
            ModService.Delete(mod, _allMods);
            RefreshMods();
            SetStatus(T("status.modDeleted") + ": " + mod.Name);
            return Task.CompletedTask;
        });
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Mods_DragEnter(object sender, DragEventArgs e) => Window_DragEnter(sender, e);

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (!EnsureGame()) return;
        await ImportPathsAsync(paths);
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        await ExecuteAsync(async () =>
        {
            foreach (var path in paths)
            {
                if (!Directory.Exists(path) && !(File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException(T("mods.dropInvalid"));
                var imported = await ModService.ImportAsync(path, GamePath, _shutdown.Token);
                SetStatus(T("status.modImported") + ": " + imported);
            }
            RefreshMods();
        });
    }

    private void BrowseResources_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("settings.resources"), InitialDirectory = Directory.Exists(ResourceRootBox.Text) ? ResourceRootBox.Text : "" };
        if (dialog.ShowDialog(this) != true) return;
        _settings.ResourceRoot = dialog.FolderName;
        ResourceRootBox.Text = dialog.FolderName;
        SettingsResourceRootBox.Text = dialog.FolderName;
        SettingsStore.Save(_settings);
    }

    private async void OpenRepair_Click(object sender, RoutedEventArgs e)
    {
        var selected = RepairModsList.Items.OfType<ModEntry>().Where(mod => mod.IsRepairSelected).Select(mod => mod.Folder).ToArray();
        if (!EnsureGame()) return;
        var gamePath = GamePath;
        if (selected.Length == 0) { RepairStatusText.Text = T("repair.selectOne"); return; }
        var root = ResourceRootBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root)) { RepairStatusText.Text = T("repair.chooseResources"); return; }

        await ExecuteAsync(async () =>
        {
            Directory.CreateDirectory(root);
            RepairProgressBar.Visibility = Visibility.Visible;
            RepairProgressBar.IsIndeterminate = true;
            RepairButton.IsEnabled = false;
            var progress = new Progress<string>(value =>
            {
                RepairStatusText.Text = value;
                SetStatus(value);
            });
            try
            {
                var result = await Task.Run(() => IntegratedRepairService.RepairAsync(gamePath, root, selected, progress, _shutdown.Token), _shutdown.Token);
                RepairStatusText.Text = string.Format(T("repair.complete"), result.Repaired, result.Failed, result.Workspace);
                SetStatus(string.Format(T("repair.completeShort"), result.Repaired, result.Failed));
                RefreshMods();
            }
            finally
            {
                RepairProgressBar.IsIndeterminate = false;
                RepairProgressBar.Visibility = Visibility.Collapsed;
                RepairButton.IsEnabled = true;
            }
        }, showError: false);
    }

    private void SelectAllRepair_Click(object sender, RoutedEventArgs e)
    {
        var mods = RepairModsList.Items.OfType<ModEntry>().ToArray();
        var selectAll = mods.Any(mod => !mod.IsRepairSelected);
        foreach (var mod in mods) mod.IsRepairSelected = selectAll;
        UpdateRepairSelectionToggleText();
    }

    private void RepairSelection_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkbox || checkbox.DataContext is not ModEntry mod) return;
        if (!RepairModsList.CaptureMouse()) return;

        _repairSelectionSweepValue = !mod.IsRepairSelected;
        _repairSelectionSweepActive = true;
        _repairSelectionSweepLastIndex = RepairModsList.Items.IndexOf(mod);
        SetRepairSelection(mod, _repairSelectionSweepValue);
        e.Handled = true;
    }

    private void RepairModsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_repairSelectionSweepActive || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(RepairModsList);
        var hit = VisualTreeHelper.HitTest(RepairModsList, point)?.VisualHit;
        var row = FindVisualAncestor<DataGridRow>(hit);
        if (row?.Item is not ModEntry mod) return;

        var index = RepairModsList.Items.IndexOf(mod);
        if (index < 0 || _repairSelectionSweepLastIndex < 0) return;
        var step = Math.Sign(index - _repairSelectionSweepLastIndex);
        for (var current = _repairSelectionSweepLastIndex + step; step != 0 && current != index + step; current += step)
            if (RepairModsList.Items[current] is ModEntry crossed) SetRepairSelection(crossed, _repairSelectionSweepValue);
        _repairSelectionSweepLastIndex = index;
    }

    private void RepairModsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_repairSelectionSweepActive) return;
        _repairSelectionSweepActive = false;
        _repairSelectionSweepLastIndex = -1;
        if (RepairModsList.IsMouseCaptured) RepairModsList.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void RepairModsList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _repairSelectionSweepActive = false;
        _repairSelectionSweepLastIndex = -1;
    }

    private void SetRepairSelection(ModEntry mod, bool selected)
    {
        if (mod.IsRepairSelected == selected) return;
        mod.IsRepairSelected = selected;
        UpdateRepairSelectionToggleText();
    }

    private void UpdateRepairSelectionToggleText()
    {
        var mods = RepairModsList.Items.OfType<ModEntry>().ToArray();
        RepairSelectionToggleText.Text = mods.Length > 0 && mods.All(mod => mod.IsRepairSelected)
            ? T("repair.deselectAll")
            : T("repair.selectAll");
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.English = EnglishRadio.IsChecked == true;
        LanguageService.Instance.IsEnglish = _settings.English;
        SettingsStore.Save(_settings);
        RefreshDllState();
        RefreshMods(ModsList.SelectedItem is ModEntry mod ? mod.Folder : null);
        SetStatus(T("footer.ready"));
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        if (MainTabs.SelectedIndex == 1) RefreshMods(ModsList.SelectedItem is ModEntry selected ? selected.Folder : null);
        else if (MainTabs.SelectedIndex == 2) RefreshMods(ModsList.SelectedItem is ModEntry selected ? selected.Folder : null);
    }

    private bool EnsureGame()
    {
        if (HasGame) return true;
        MessageBox.Show(this, T("status.gameMissing"), T("dialog.title"), MessageBoxButton.OK, MessageBoxImage.Information);
        MainTabs.SelectedIndex = 0;
        return false;
    }
}
