using System.Collections.ObjectModel;
using System.Windows;
using SonarPresetSwitcher.Models;
using SonarPresetSwitcher.Services;

namespace SonarPresetSwitcher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxActivityLines = 100;

    private readonly SonarClient _client;
    private readonly ProcessWatcher _watcher;
    private readonly PresetCoordinator _coordinator;
    private readonly AudioDeviceService _audioDevices;
    private readonly AudioRoutingService _routing;
    private readonly AppSettings _settings;

    private bool _connected;
    private string _statusText = "Connecting to Sonar…";
    private string _currentPresetText = "—";
    private string _activeMappingText = "No mapped game running";
    private SonarConfig? _defaultPreset;
    private AudioDevice? _defaultOutputDevice;
    private bool _loading;

    public MainViewModel(
        SonarClient client,
        ProcessWatcher watcher,
        PresetCoordinator coordinator,
        AudioDeviceService audioDevices,
        AudioRoutingService routing,
        AppSettings settings)
    {
        _client = client;
        _watcher = watcher;
        _coordinator = coordinator;
        _audioDevices = audioDevices;
        _routing = routing;
        _settings = settings;

        foreach (var mapping in settings.Mappings)
            Mappings.Add(CreateRow(mapping));

        AddMappingCommand = new RelayCommand(AddMapping);
        RemoveMappingCommand = new RelayCommand(RemoveMapping);
        RefreshCommand = new RelayCommand(async () => await RefreshAsync());

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.Activity += OnActivity;
        _routing.Activity += OnActivity;
    }

    public ObservableCollection<MappingRowViewModel> Mappings { get; } = [];

    /// <summary>"My Presets" — every Game-channel preset, favourites first.</summary>
    public ObservableCollection<SonarConfig> Presets { get; } = [];

    /// <summary>Output devices for the global default picker: system default, then real devices.</summary>
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];

    /// <summary>Same list for mapping rows, with the "use the global default" entry in front.</summary>
    public ObservableCollection<AudioDevice> MappingOutputDevices { get; } = [];

    public ObservableCollection<string> ActivityLog { get; } = [];

    public RelayCommand AddMappingCommand { get; }

    public RelayCommand RemoveMappingCommand { get; }

    public RelayCommand RefreshCommand { get; }

    /// <summary>False when Windows will not expose the per-app routing API; the pickers grey out.</summary>
    public bool AudioRoutingSupported => _routing.IsSupported;

    public string OutputDeviceTooltip => AudioRoutingSupported
        ? "Sends this app's audio to a specific output device, exactly like Settings → Sound → " +
          "Volume mixer. The Windows default device is left alone.\n\n" +
          "Windows remembers the choice per executable, so it also applies the next time the game " +
          "launches — and a game that is already playing audio may need a restart to move over."
        : "Per-app audio routing is not available on this build of Windows.";

    public bool Connected
    {
        get => _connected;
        private set => SetField(ref _connected, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string CurrentPresetText
    {
        get => _currentPresetText;
        private set => SetField(ref _currentPresetText, value);
    }

    public string ActiveMappingText
    {
        get => _activeMappingText;
        private set => SetField(ref _activeMappingText, value);
    }

    public bool HasMappings => Mappings.Count > 0;

    // ---- settings-backed properties -------------------------------------------------------

    public SonarConfig? DefaultPreset
    {
        get => _defaultPreset;
        set
        {
            if (!SetField(ref _defaultPreset, value) || _loading)
                return;

            _settings.DefaultPresetId = value?.Id ?? "";
            _settings.DefaultPresetName = value?.Name ?? "";
            Persist();
        }
    }

    public AudioDevice? DefaultOutputDevice
    {
        get => _defaultOutputDevice;
        set
        {
            if (!SetField(ref _defaultOutputDevice, value) || _loading)
                return;

            _settings.DefaultOutputDeviceId = value?.Id ?? AudioDevice.SystemDefaultId;
            _settings.DefaultOutputDeviceName = value is null || value.IsSentinel ? "" : value.Name;
            Persist();
        }
    }

    public bool ShowNotifications
    {
        get => _settings.ShowNotifications;
        set
        {
            if (_settings.ShowNotifications == value)
                return;

            _settings.ShowNotifications = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
                return;

            _settings.StartWithWindows = value;
            StartupRegistration.SetEnabled(value);
            OnPropertyChanged();
            Persist();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value)
                return;

            _settings.MinimizeToTray = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public int PollIntervalSeconds
    {
        get => _settings.PollIntervalSeconds;
        set
        {
            if (_settings.PollIntervalSeconds == value)
                return;

            _settings.PollIntervalSeconds = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool MonitoringPaused
    {
        get => _watcher.IsPaused;
        set
        {
            if (_watcher.IsPaused == value)
                return;

            _watcher.IsPaused = value;
            OnPropertyChanged();
            Log(value ? "Monitoring paused." : "Monitoring resumed.");
        }
    }

    // ---- lifecycle -------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        StartWithWindows = StartupRegistration.IsEnabled();
        _watcher.IntervalSeconds = _settings.ClampedPollInterval;

        if (!AudioRoutingSupported)
            Log("Per-app audio routing is unavailable on this build of Windows.");

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        ApplyOutputDevices(_audioDevices.GetRenderDevices());

        var presets = await _coordinator.RefreshPresetsAsync();
        ApplyPresets(presets);

        RefreshRunningFlags();
    }

    private void ApplyPresets(IReadOnlyList<SonarConfig> presets)
    {
        _loading = true;
        try
        {
            Presets.Clear();
            foreach (var preset in presets)
                Presets.Add(preset);

            foreach (var row in Mappings)
                row.SyncPreset(presets);

            DefaultPreset = presets.FirstOrDefault(p =>
                string.Equals(p.Id, _settings.DefaultPresetId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Rebuilds both device pickers. Devices referenced by a saved mapping but missing from the
    /// enumeration are kept as "(not connected)" entries, so an unplugged headset shows the user what
    /// their setting still says instead of silently blanking the combo.
    /// </summary>
    private void ApplyOutputDevices(IReadOnlyList<AudioDevice> devices)
    {
        _loading = true;
        try
        {
            var known = devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var (id, name) in SavedDeviceReferences())
            {
                if (!string.IsNullOrEmpty(id) && !known.ContainsKey(id))
                    known[id] = new AudioDevice(id, $"{(string.IsNullOrWhiteSpace(name) ? "Unknown device" : name)} (not connected)");
            }

            var ordered = known.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            OutputDevices.Clear();
            OutputDevices.Add(AudioDevice.SystemDefault);

            MappingOutputDevices.Clear();
            MappingOutputDevices.Add(AudioDevice.Inherit);
            MappingOutputDevices.Add(AudioDevice.SystemDefault);

            foreach (var device in ordered)
            {
                OutputDevices.Add(device);
                MappingOutputDevices.Add(device);
            }

            foreach (var row in Mappings)
                row.SyncOutputDevice(MappingOutputDevices);

            DefaultOutputDevice = OutputDevices.FirstOrDefault(d =>
                string.Equals(d.Id, _settings.DefaultOutputDeviceId, StringComparison.OrdinalIgnoreCase))
                ?? AudioDevice.SystemDefault;
        }
        finally
        {
            _loading = false;
        }
    }

    private IEnumerable<(string Id, string Name)> SavedDeviceReferences()
    {
        yield return (_settings.DefaultOutputDeviceId, _settings.DefaultOutputDeviceName);

        foreach (var mapping in _settings.Mappings)
        {
            if (!string.Equals(mapping.OutputDeviceId, AudioDevice.InheritId, StringComparison.Ordinal))
                yield return (mapping.OutputDeviceId, mapping.OutputDeviceName);
        }
    }

    /// <summary>Updates the green "running" dots. Called on a UI timer from the window.</summary>
    public void RefreshRunningFlags()
    {
        var running = _watcher.RunningProcesses;
        foreach (var row in Mappings)
            row.IsRunning = running.Contains(GameMapping.NormalizeProcessName(row.Mapping.ProcessName));
    }

    // ---- commands --------------------------------------------------------------------------

    private void AddMapping()
    {
        var dialog = new Views.AddMappingDialog(Presets)
        {
            Owner = Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        var mapping = dialog.Result;

        var existing = Mappings.FirstOrDefault(r =>
            string.Equals(r.Mapping.ProcessName, mapping.ProcessName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            MessageBox.Show(
                Application.Current.MainWindow!,
                $"{mapping.ProcessName}.exe is already mapped to \"{existing.Mapping.PresetName}\".",
                "Already mapped",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _settings.Mappings.Add(mapping);
        var row = CreateRow(mapping);
        row.SyncPreset(Presets.ToList());
        row.SyncOutputDevice(MappingOutputDevices);
        Mappings.Add(row);
        OnPropertyChanged(nameof(HasMappings));

        Log($"Mapped {mapping.DisplayName} ({mapping.ProcessName}.exe) to \"{mapping.PresetName}\".");
        RefreshRunningFlags();
        Persist();
    }

    private void RemoveMapping(object? parameter)
    {
        if (parameter is not MappingRowViewModel row)
            return;

        // Hand the game back to the system default rather than leaving a persisted override behind
        // for a mapping the user just deleted.
        row.Mapping.OutputDeviceId = AudioDevice.SystemDefaultId;
        _routing.Apply(row.Mapping, _settings);
        _routing.Forget(row.Mapping.ProcessName);

        _settings.Mappings.Remove(row.Mapping);
        Mappings.Remove(row);
        OnPropertyChanged(nameof(HasMappings));

        Log($"Removed mapping for {row.DisplayName}.");
        Persist();
    }

    private MappingRowViewModel CreateRow(GameMapping mapping) => new(mapping, Persist);

    private void Persist()
    {
        SettingsStore.Save(_settings);
        _coordinator.UpdateSettings(_settings);
    }

    // ---- coordinator events ------------------------------------------------------------------

    private void OnCoordinatorStateChanged(CoordinatorState state)
    {
        Dispatch(() =>
        {
            Connected = state.Connected;
            StatusText = state.Connected
                ? $"Connected — {state.Endpoint}"
                : "Sonar not running";
            CurrentPresetText = state.CurrentPresetName ?? "—";
            ActiveMappingText = state.ActiveMappingName is null
                ? "No mapped game running"
                : $"Active: {state.ActiveMappingName}";
        });
    }

    private void OnActivity(string message) => Dispatch(() => Log(message));

    private void Log(string message)
    {
        ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (ActivityLog.Count > MaxActivityLines)
            ActivityLog.RemoveAt(ActivityLog.Count - 1);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }
}
