using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EffModManager;

internal static class ManagerUiTests
{
    private sealed record Choice(string Label, string TypeLabel);
    private sealed class Row(ListCollectionView targets)
    {
        public ListCollectionView Targets { get; } = targets;
        public object? Selection { get; set; } = targets.GetItemAt(0);
    }

    internal static void Run(string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var trace = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
            try
            {
                var app = new App(launchMainWindow: false);
                app.InitializeComponent();
                var window = new MainWindow(new AppSettings { GamePath = Path.Combine(root, "no-game") }, startBackgroundTasks: false);
                var tabs = (TabControl)window.FindName("MainTabs");
                tabs.SelectedIndex = 4;
                var timeout = new TimeoutException("UI fixture: download timed out");
                ActivityLog.Default.Failure("log.index", timeout, RecoveryHelp.Index(Path.Combine(root, "resources"), "workspace", "https://github.com/ssice-a/EFF/releases"));
                Render(window, "logs");
                var details = (TextBlock)window.FindName("LogMessage");
                var recovery = (TextBlock)window.FindName("LogRecovery");
                var links = (ItemsControl)window.FindName("LogLinks");
                if (!details.Text.Contains("UI fixture") || recovery.Text.Length < 80 || links.Items.Count < 1)
                    throw new InvalidOperationException("Log selection did not populate the original error, instructions and links.");
                LanguageService.Instance.IsEnglish = true;
                Render(window, "logs-en");
                if (!recovery.Text.Contains("Import local index")) throw new InvalidOperationException("Visible log recovery did not follow language switching.");

                LanguageService.Instance.IsEnglish = false;
                tabs.SelectedIndex = 1;
                ((StackPanel)window.FindName("WeaponPanel")).Visibility = Visibility.Visible;
                ((TextBlock)window.FindName("DetailsName")).Text = "Weapon UI fixture";
                var list = (ItemsControl)window.FindName("WeaponTargetsList");
                list.IsEnabled = true;
                var choices = new ListCollectionView(new[] { new Choice("Test weapon", "sword"), new Choice("Another weapon", "sword") });
                choices.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Choice.TypeLabel)));
                list.ItemsSource = new[] { new Row(choices) };
                Render(window, "weapon");
                var combos = Descendants<ComboBox>(list).ToArray();
                if (combos.Length != 1 || combos[0].SelectedItem is null || window.FindName("WeaponBindingBox") is not null)
                    throw new InvalidOperationException("Weapon editor did not render exactly one initialized replacement selector.");
                if (trace.Errors.Count != 0) throw new InvalidOperationException("WPF binding failures: " + string.Join("\n", trace.Errors));
                if (app.Windows.Count != 1) throw new InvalidOperationException("UI regression accidentally launched a second application window.");
                window.Close();
                app.Shutdown();
                Console.WriteLine("PASS: real WPF log/weapon pages render with initialized target selection, localized recovery links and no binding errors.");
            }
            catch (Exception error) { failure = error; }
            finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("WPF UI regression timed out.");
        if (failure is not null) throw new InvalidOperationException("WPF UI regression failed.", failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Render(MainWindow window, string name)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1240, 820));
        content.Arrange(new Rect(0, 0, 1240, 820));
        content.UpdateLayout();
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1240, 820, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        if (Environment.GetEnvironmentVariable("EFF_MANAGER_UI_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            using var file = File.Create(Path.Combine(folder, name + ".png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
        }
    }

    private sealed class BindingErrors : TraceListener
    {
        internal List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
