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
    private DefaultDeviceGuard? _defaultGuard;
    private PresetCoordinator? _coordinator;
    private ChannelBoostService? _channelBoosts;
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

        ActivityFileLog.StartSession(e.Args);

        var settings = SettingsStore.Load();

        // A logon entry left pointing at a path that no longer launches (moved exe, or the dotnet muxer
        // recorded by an older build) fails silently at boot, so fix it while we know where we live.
        StartupRegistration.RepairIfBroken();

        _discovery = new SonarDiscovery();
        _client = new SonarClient(_discovery);
        _watcher = new ProcessWatcher { IntervalSeconds = settings.ClampedPollInterval };
        _audioDevices = new AudioDeviceService();
        _routing = new AudioRoutingService(_audioDevices);
        _defaultGuard = new DefaultDeviceGuard(_audioDevices, _watcher, settings);
        _coordinator = new PresetCoordinator(_client, _watcher, _routing, settings);
        _channelBoosts = new ChannelBoostService(_client, settings);

        var viewModel = new MainViewModel(
            _client, _watcher, _coordinator, _audioDevices, _routing, _defaultGuard,
            _channelBoosts, settings);
        var window = new MainWindow(viewModel);
        MainWindow = window;

        _tray = new TrayIconHost(window, viewModel, _coordinator);

        var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized)
            window.Show();

        _coordinator.Start();
        _watcher.Start();
        _defaultGuard.Start();
        _channelBoosts.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _coordinator?.Dispose();
        _defaultGuard?.Dispose();
        _channelBoosts?.Dispose();
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
