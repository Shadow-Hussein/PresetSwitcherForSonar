using System.Windows;
using SonarPresetSwitcher.Services;
using SonarPresetSwitcher.ViewModels;
using SonarPresetSwitcher.Views;

namespace SonarPresetSwitcher;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\SonarPresetSwitcher.SingleInstance";

    private Mutex? _instanceMutex;
    private SonarDiscovery? _discovery;
    private SonarClient? _client;
    private ProcessWatcher? _watcher;
    private AudioDeviceService? _audioDevices;
    private AudioRoutingService? _routing;
    private PresetCoordinator? _coordinator;
    private TrayIconHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // A second launch (e.g. from the Start menu while already in the tray) just exits.
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        var settings = SettingsStore.Load();

        _discovery = new SonarDiscovery();
        _client = new SonarClient(_discovery);
        _watcher = new ProcessWatcher { IntervalSeconds = settings.ClampedPollInterval };
        _audioDevices = new AudioDeviceService();
        _routing = new AudioRoutingService(_audioDevices);
        _coordinator = new PresetCoordinator(_client, _watcher, _routing, settings);

        var viewModel = new MainViewModel(_client, _watcher, _coordinator, _audioDevices, _routing, settings);
        var window = new MainWindow(viewModel);
        MainWindow = window;

        _tray = new TrayIconHost(window, viewModel, _coordinator);

        var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized)
            window.Show();

        _coordinator.Start();
        _watcher.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _coordinator?.Dispose();
        _routing?.Dispose();
        _watcher?.Dispose();
        _client?.Dispose();
        _discovery?.Dispose();

        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
