using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SonarPresetSwitcher.ViewModels;

namespace SonarPresetSwitcher.Views;

public partial class MainWindow : Window
{
    private const string ProjectUrl = "https://github.com/Shadow-Hussein/SonarPresetSwitcher";

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

    // Caption buttons. Close goes through Close() rather than shutting down, so the minimize-to-tray
    // decision in OnClosing still applies exactly as it did with the system close button.
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnOpenProjectPage(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute is what hands the URL to the default browser; without it .NET tries to
            // execute it as a file and throws.
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // No browser association, or the shell refused. Show the address so it can be copied.
            MessageBox.Show(
                this,
                $"Could not open the browser ({ex.Message}).\n\n{ProjectUrl}",
                "Sonar Preset Switcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

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
