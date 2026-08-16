using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SonarPresetSwitcher.ViewModels;

namespace SonarPresetSwitcher.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _runningIndicatorTimer;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // Keeps the per-row "running" dots in step with the process watcher.
        _runningIndicatorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _runningIndicatorTimer.Tick += (_, _) => _viewModel.RefreshRunningFlags();
        _runningIndicatorTimer.Start();

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    /// <summary>Set by the tray host when the user really wants to quit.</summary>
    public bool ForceClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ForceClose && _viewModel.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _runningIndicatorTimer.Stop();
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }
}
