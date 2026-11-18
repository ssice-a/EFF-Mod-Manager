using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EffModManager;

public partial class MainWindow
{
    private sealed class WeaponSelection(WeaponBinding binding, ListCollectionView targets, ChoiceItem? selection)
    {
        public WeaponBinding Binding { get; } = binding;
        public ListCollectionView Targets { get; } = targets;
        public ChoiceItem? Selection { get; set; } = selection;
    }
    private sealed record ChoiceItem(WeaponChoice Choice, string Label, string TypeLabel);
    private Task<WeaponListing>? _weaponCatalogTask;
    private string _weaponCatalogLocation = "";
    private int _weaponDetailsVersion;
    private bool _weaponApplyBusy;

    private void ClearWeaponDetails()
    {
        _weaponDetailsVersion++;
        WeaponPanel.Visibility = Visibility.Collapsed;
        WeaponTargetsList.IsEnabled = false;
        WeaponTargetsList.ItemsSource = null;
    }

    private async Task ShowWeaponDetailsAsync(ModEntry mod, bool refresh = false)
    {
        var version = ++_weaponDetailsVersion;
        WeaponTargetsList.IsEnabled = false;
        WeaponTargetsList.ItemsSource = null;
        try
        {
            var bindings = WeaponRetargeting.Bindings(mod.IniPath);
            WeaponPanel.Visibility = bindings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (bindings.Count == 0) return;
            WeaponStatus.Text = T("weapon.loading");
            var game = GamePath;
            var resources = _settings.ResourceRoot;
            var location = game + "|" + resources + "|" + SharedResources.IndexWorkspace;
            if (refresh || _weaponCatalogTask is null || _weaponCatalogTask.IsFaulted || _weaponCatalogTask.IsCanceled || location != _weaponCatalogLocation)
            {
                _weaponCatalogLocation = location;
                _weaponCatalogTask = Task.Run(() => WeaponCatalog.Listing(game, resources, _shutdown.Token, refresh), _shutdown.Token);
            }
            var catalog = await _weaponCatalogTask;
            if (version != _weaponDetailsVersion || !ReferenceEquals(ModsList.SelectedItem, mod)) return;
            var english = _settings.English;
            var ini = ModService.ParseIni(File.ReadAllLines(mod.IniPath));
            // One editable target per binding; no redundant source selector. Multiple-target
            // Mods retain their separate relations instead of silently retargeting all of them.
            WeaponTargetsList.ItemsSource = bindings.Select(binding =>
            {
                var choices = new ListCollectionView(WeaponChoice.Ordered(catalog.Choices, english)
                    .Select(c => new ChoiceItem(c, c.Label(english), c.TypeLabel(english))).ToArray());
                choices.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChoiceItem.TypeLabel)));
                var prefab = binding.Sections.Where(ini.ContainsKey)
                    .Select(s => ini[s].GetValueOrDefault("target.prefab", "")).FirstOrDefault(p => p.Length > 0);
                var selected = choices.Cast<ChoiceItem>().FirstOrDefault(c => c.Choice.Prefab.Equals(prefab, StringComparison.OrdinalIgnoreCase)) ??
                    choices.Cast<ChoiceItem>().FirstOrDefault(c => c.Choice.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase));
                return new WeaponSelection(binding, choices, selected);
            }).ToArray();
            WeaponStatus.Text = T(bindings.All(b => b.Sections.All(s => s.StartsWith("AttachEffect", StringComparison.OrdinalIgnoreCase)))
                ? "weapon.mountHint" : "weapon.hint");
            WeaponTargetsList.IsEnabled = !_weaponApplyBusy;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version == _weaponDetailsVersion)
            {
                WeaponStatus.Text = error.Message + " " + T("log.seeLogs");
                ActivityLog.Default.Failure("log.weaponCatalog", error, RecoveryHelp.Index(_settings.ResourceRoot));
            }
        }
    }

    private async void WeaponCatalogReload_Click(object sender, RoutedEventArgs e)
    {
        if (ModsList.SelectedItem is not ModEntry mod) return;
        if (_weaponCatalogTask is { IsCompleted: false }) return;
        _weaponCatalogTask = null;
        await ShowWeaponDetailsAsync(mod, refresh: true);
    }

    private async void WeaponApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _weaponApplyBusy || ModsList.SelectedItem is not ModEntry mod || (sender as FrameworkElement)?.DataContext is not WeaponSelection selection ||
            selection.Selection is not ChoiceItem selected || _weaponCatalogTask is null) return;
        WeaponTargetsList.IsEnabled = false;
        _weaponApplyBusy = true;
        try
        {
            var original = File.ReadAllBytes(mod.IniPath);
            var catalog = await _weaponCatalogTask;
            await Task.Run(() =>
            {
                var target = catalog.Resolve(selected.Choice, _shutdown.Token);
                _shutdown.Token.ThrowIfCancellationRequested();
                WeaponRetargeting.Apply(mod, selection.Binding, target, original);
            }, _shutdown.Token);
            if (ReferenceEquals(ModsList.SelectedItem, mod)) await ShowWeaponDetailsAsync(mod);
            ActivityLog.Default.Info("log.weaponApply", mod.Name + " -> " + selected.Label);
            SetStatus(T("weapon.saved"));
            if (ReferenceEquals(ModsList.SelectedItem, mod)) WeaponStatus.Text = T("weapon.saved");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            ActivityLog.Default.Failure("log.weaponApply", error);
            WeaponStatus.Text = error.Message + " " + T("log.seeLogs");
            SetStatus(WeaponStatus.Text);
        }
        finally
        {
            _weaponApplyBusy = false;
            WeaponTargetsList.IsEnabled = WeaponPanel.Visibility == Visibility.Visible && _weaponCatalogTask?.IsCompletedSuccessfully == true;
        }
    }
}
