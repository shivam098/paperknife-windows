using Microsoft.UI.Xaml;

namespace PaperKnife.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // Edits are transactional in PaperKnife.Core, so an unexpected UI error can't leave the
        // document half-changed. Report it instead of closing the window and losing unsaved work.
        UnhandledException += (_, e) =>
        {
            if (_window is not null)
            {
                e.Handled = true;
                _window.ShowUnexpectedError(e.Exception);
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        var path = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (path is not null)
        {
            _window.OpenOnLaunch(path);
        }
    }
}
