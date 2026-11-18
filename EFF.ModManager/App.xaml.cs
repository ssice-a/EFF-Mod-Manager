namespace EffModManager;

public partial class App : System.Windows.Application
{
    private readonly bool _launchMainWindow;
    public App() : this(true) { }
    internal App(bool launchMainWindow)
    {
        _launchMainWindow = launchMainWindow;
        DispatcherUnhandledException += (_, e) => ActivityLog.Default.Failure("log.application", e.Exception);
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_launchMainWindow) new MainWindow().Show();
    }
}
