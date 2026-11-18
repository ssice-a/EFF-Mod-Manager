using System.Windows;

namespace EffModManager;

public partial class MainWindow
{
    private sealed record CharacterDetailsItem(string Label, string ResourceCount, IReadOnlyList<CharacterTarget> Targets);
    private Task<CharacterTargets>? _characterTargetsTask;
    private string _characterTargetsGame = "";
    private int _characterDetailsVersion;

    private void ClearCharacterDetails()
    {
        _characterDetailsVersion++;
        CharacterPanel.Visibility = Visibility.Collapsed;
        CharacterTargetsList.ItemsSource = null;
        CharacterStatus.Text = "";
    }

    private void SetCharacterDetails(IReadOnlyList<CharacterBinding> bindings, CharacterTargets? catalog = null)
    {
        CharacterTargetsList.ItemsSource = (catalog?.Resolve(bindings) ?? bindings).Select(b => new CharacterDetailsItem(
            catalog?.Label(b.Identity, _settings.English) ?? b.Identity,
            string.Format(T("character.resources"), b.Targets.Count), b.Targets)).ToArray();
    }

    private async Task ShowCharacterDetailsAsync(ModEntry mod)
    {
        ClearCharacterDetails();
        var version = _characterDetailsVersion;
        try
        {
            var bindings = CharacterTargets.Read(mod.IniPath);
            if (bindings.Count == 0) return;
            CharacterPanel.Visibility = Visibility.Visible;
            SetCharacterDetails(bindings);
            CharacterStatus.Text = T("character.loading");
            var game = GamePath;
            if (_characterTargetsTask is null || _characterTargetsTask.IsFaulted || _characterTargetsTask.IsCanceled || game != _characterTargetsGame)
            {
                _characterTargetsGame = game;
                _characterTargetsTask = Task.Run(() => CharacterTargets.Load(game, _shutdown.Token), _shutdown.Token);
            }
            var catalog = await _characterTargetsTask;
            if (version != _characterDetailsVersion || !ReferenceEquals(ModsList.SelectedItem, mod)) return;
            SetCharacterDetails(bindings, catalog);
            CharacterStatus.Text = "";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version == _characterDetailsVersion)
            {
                CharacterStatus.Text = string.Format(T("character.namesUnavailable"), error.Message);
                ActivityLog.Default.Failure("log.characterCatalog", error, warning: true);
            }
        }
    }
}
