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
    private readonly DefaultDeviceGuard _defaultGuard;
    private readonly AppSettings _settings;

    private bool _connected;
    private string _statusText = "Connecting to Sonar…";
    private string _currentPresetText = "—";
    private string _activeMappingText = "No mapped game running";
    private SonarConfig? _defaultPreset;
    private AudioDevice? _defaultOutputDevice;
    private AudioDevice? _keepWindowsDefaultDevice;
    private bool _loading;

    public MainViewModel(
        SonarClient client,
        ProcessWatcher watcher,
        PresetCoordinator coordinator,
        AudioDeviceService audioDevices,
        AudioRoutingService routing,
        DefaultDeviceGuard defaultGuard,
        AppSettings settings)
    {
        _client = client;
        _watcher = watcher;
        _coordinator = coordinator;
        _audioDevices = audioDevices;
        _routing = routing;
        _defaultGuard = defaultGuard;
        _settings = settings;

        foreach (var mapping in settings.Mappings)
            Mappings.Add(CreateRow(mapping));

        AddMappingCommand = new RelayCommand(AddMapping);
        RemoveMappingCommand = new RelayCommand(RemoveMapping);
        RefreshCommand = new RelayCommand(async () => await RefreshAsync());

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.Activity += OnActivity;
        _routing.Activity += OnActivity;
        _defaultGuard.Activity += OnActivity;
        _defaultGuard.SupportChanged += OnDefaultGuardSupportChanged;
    }

    public ObservableCollection<MappingRowViewModel> Mappings { get; } = [];

    /// <summary>"My Presets" — every Game-channel preset, favourites first.</summary>
    public ObservableCollection<SonarConfig> Presets { get; } = [];

    /// <summary>Output devices for the global default picker: system default, then real devices.</summary>
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];

    /// <summary>Same list for mapping rows, with the "use the global default" entry in front.</summary>
    public ObservableCollection<AudioDevice> MappingOutputDevices { get; } = [];

    /// <summary>Real devices only — "system default" is meaningless when you are picking what it is.</summary>
    public ObservableCollection<AudioDevice> SystemDefaultDevices { get; } = [];

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

    /// <summary>False when Windows will not let us move the default device; the row greys out.</summary>
    public bool KeepWindowsDefaultSupported => _defaultGuard.IsSupported;

    public string KeepWindowsDefaultTooltip => KeepWindowsDefaultSupported
        ? "Puts the Windows default playback device back to this one whenever another app changes " +
          "it.\n\nSteelSeries GG re-claims the default for \"Sonar - Gaming\" every time it starts, " +
          "which is why the device you set by hand is gone after a reboot; GG has no setting to stop " +
          "that, so this changes it back instead.\n\n" +
          "This is the one option that writes to Windows itself rather than a per-app override. While " +
          "it is on, changing the default from the Sound settings will not stick — untick it first."
        : "Changing the Windows default device is not available on this build of Windows.";

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

    /// <summary>
    /// Ticking this on with nothing chosen yet adopts whatever Windows is using right now, so the
    /// checkbox on its own means "stop letting other apps move this".
    /// </summary>
    public bool KeepWindowsDefault
    {
        get => _settings.KeepWindowsDefault;
        set
        {
            if (_settings.KeepWindowsDefault == value)
                return;

            _settings.KeepWindowsDefault = value;
            OnPropertyChanged();

            if (value && KeepWindowsDefaultDevice is null)
            {
                var current = _audioDevices.GetDefaultRenderDeviceId();
                var device = SystemDefaultDevices.FirstOrDefault(d =>
                    string.Equals(d.Id, current, StringComparison.OrdinalIgnoreCase));

                if (device is not null)
                {
                    // Assigning the device persists on its own; do not save twice.
                    KeepWindowsDefaultDevice = device;
                    return;
                }

                Log("Pick the output device to hold before turning this on.");
            }

            Persist();
        }
    }

    public AudioDevice? KeepWindowsDefaultDevice
    {
        get => _keepWindowsDefaultDevice;
        set
        {
            if (!SetField(ref _keepWindowsDefaultDevice, value) || _loading)
                return;

            _settings.KeepWindowsDefaultDeviceId = value?.Id ?? "";
            _settings.KeepWindowsDefaultDeviceName = value?.Name ?? "";
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

        if (!KeepWindowsDefaultSupported)
            Log("Holding the Windows default playback device is unavailable on this build of Windows.");

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
    /// Rebuilds the three device pickers.
    ///
    /// A saved id that no longer enumerates does not mean the device is gone: Sonar re-creates its
    /// virtual endpoints under new ids whenever GG rebuilds its driver, and a headset moved to another
    /// USB port looks identical. <see cref="RepairSavedDeviceReferences"/> re-points such a setting at
    /// the live endpoint carrying the same name — the same recovery <see cref="DefaultDeviceGuard"/>
    /// performs for itself, done here so it also reaches settings.json.
    ///
    /// Whatever is still unresolved after that stays visible as a "(not connected)" entry, so an
    /// unplugged headset shows the user what their setting says instead of silently blanking the combo.
    /// Each picker only carries its own: choosing a device to hold should not be a list of every dead
    /// endpoint some mapping still remembers.
    /// </summary>
    private void ApplyOutputDevices(IReadOnlyList<AudioDevice> devices)
    {
        var known = devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var repaired = RepairSavedDeviceReferences(devices, known);

        _loading = true;
        try
        {
            OutputDevices.Clear();
            OutputDevices.Add(AudioDevice.SystemDefault);
            foreach (var device in WithMissing(devices, known, [DefaultOutputReference()]))
                OutputDevices.Add(device);

            MappingOutputDevices.Clear();
            MappingOutputDevices.Add(AudioDevice.Inherit);
            MappingOutputDevices.Add(AudioDevice.SystemDefault);
            foreach (var device in WithMissing(devices, known, MappingReferences()))
                MappingOutputDevices.Add(device);

            SystemDefaultDevices.Clear();
            foreach (var device in WithMissing(devices, known, [KeepWindowsDefaultReference()]))
                SystemDefaultDevices.Add(device);

            foreach (var row in Mappings)
                row.SyncOutputDevice(MappingOutputDevices);

            DefaultOutputDevice = OutputDevices.FirstOrDefault(d =>
                string.Equals(d.Id, _settings.DefaultOutputDeviceId, StringComparison.OrdinalIgnoreCase))
                ?? AudioDevice.SystemDefault;

            KeepWindowsDefaultDevice = SystemDefaultDevices.FirstOrDefault(d =>
                string.Equals(d.Id, _settings.KeepWindowsDefaultDeviceId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _loading = false;
        }

        // Deliberately outside the _loading window: this is the one write here that is a real change to
        // the settings and has to reach the coordinator and the guard, not just the combo boxes.
        if (repaired)
            Persist();
    }

    /// <summary>
    /// The live devices, plus a "(not connected)" placeholder for each of <paramref name="referenced"/>
    /// that is not among them. Sentinels are added by the caller and are not passed through here.
    /// </summary>
    private static List<AudioDevice> WithMissing(
        IReadOnlyList<AudioDevice> live,
        IReadOnlyDictionary<string, AudioDevice> known,
        IEnumerable<(string Id, string Name)> referenced)
    {
        var result = new List<AudioDevice>(live);
        var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, name) in referenced)
        {
            if (string.IsNullOrEmpty(id) || id == AudioDevice.InheritId || known.ContainsKey(id))
                continue;

            if (!placeholders.Add(id))
                continue;

            var label = AudioDevice.CleanName(name);
            result.Add(new AudioDevice(id, label.Length == 0 ? "Unknown device" : label) { IsMissing = true });
        }

        return result.OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Brings every saved device reference back in line with what Windows currently reports: the name is
    /// refreshed from the live endpoint, and an id that no longer resolves is re-pointed at the endpoint
    /// carrying the same name. Returns true when anything moved, in which case the caller saves.
    /// </summary>
    private bool RepairSavedDeviceReferences(
        IReadOnlyList<AudioDevice> devices, IReadOnlyDictionary<string, AudioDevice> known)
    {
        (string Id, string Name) Repair(string id, string name)
        {
            var cleaned = AudioDevice.CleanName(name);

            if (string.IsNullOrEmpty(id) || id == AudioDevice.InheritId)
                return (id, cleaned);

            if (!known.TryGetValue(id, out var live))
            {
                var moved = AudioDeviceService.FindUniqueId(devices, cleaned);
                if (moved is null)
                    return (id, cleaned);

                id = moved;
                live = known[moved];
            }

            return (id, live.Name);
        }

        var changed = false;

        var (defaultId, defaultName) = Repair(_settings.DefaultOutputDeviceId, _settings.DefaultOutputDeviceName);
        if (defaultId != _settings.DefaultOutputDeviceId || defaultName != _settings.DefaultOutputDeviceName)
        {
            _settings.DefaultOutputDeviceId = defaultId;
            _settings.DefaultOutputDeviceName = defaultName;
            changed = true;
        }

        var (holdId, holdName) = Repair(_settings.KeepWindowsDefaultDeviceId, _settings.KeepWindowsDefaultDeviceName);
        if (holdId != _settings.KeepWindowsDefaultDeviceId || holdName != _settings.KeepWindowsDefaultDeviceName)
        {
            _settings.KeepWindowsDefaultDeviceId = holdId;
            _settings.KeepWindowsDefaultDeviceName = holdName;
            changed = true;
        }

        foreach (var mapping in _settings.Mappings)
        {
            var (id, name) = Repair(mapping.OutputDeviceId, mapping.OutputDeviceName);
            if (id == mapping.OutputDeviceId && name == mapping.OutputDeviceName)
                continue;

            mapping.OutputDeviceId = id;
            mapping.OutputDeviceName = name;
            changed = true;
        }

        return changed;
    }

    private (string Id, string Name) DefaultOutputReference() =>
        (_settings.DefaultOutputDeviceId, _settings.DefaultOutputDeviceName);

    private (string Id, string Name) KeepWindowsDefaultReference() =>
        (_settings.KeepWindowsDefaultDeviceId, _settings.KeepWindowsDefaultDeviceName);

    private IEnumerable<(string Id, string Name)> MappingReferences()
    {
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
        _defaultGuard.UpdateSettings(_settings);
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

    /// <summary>
    /// Whether the default endpoint can be moved is settled a moment after startup, on the guard's own
    /// thread, so the row has to be told to re-read it — the bindings would otherwise keep the answer
    /// from before the guard's first tick.
    /// </summary>
    private void OnDefaultGuardSupportChanged() => Dispatch(() =>
    {
        OnPropertyChanged(nameof(KeepWindowsDefaultSupported));
        OnPropertyChanged(nameof(KeepWindowsDefaultTooltip));
    });

    private void Log(string message)
    {
        ActivityFileLog.Append(message);

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
