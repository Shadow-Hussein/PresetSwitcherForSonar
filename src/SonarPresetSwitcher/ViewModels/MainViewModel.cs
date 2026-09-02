using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly AudioDeviceWatcher _deviceWatcher;
    private readonly AudioRoutingService _routing;
    private readonly DefaultDeviceGuard _defaultGuard;
    private readonly ChannelBoostService _channelBoosts;
    private readonly ChatMixService _chatMix;
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
        AudioDeviceWatcher deviceWatcher,
        AudioRoutingService routing,
        DefaultDeviceGuard defaultGuard,
        ChannelBoostService channelBoosts,
        ChatMixService chatMix,
        AppSettings settings)
    {
        _client = client;
        _watcher = watcher;
        _coordinator = coordinator;
        _audioDevices = audioDevices;
        _deviceWatcher = deviceWatcher;
        _routing = routing;
        _defaultGuard = defaultGuard;
        _channelBoosts = channelBoosts;
        _chatMix = chatMix;
        _settings = settings;

        foreach (var mapping in settings.Mappings)
            Mappings.Add(CreateRow(mapping));

        foreach (var rule in settings.ChannelBoosts)
            ChannelBoosts.Add(CreateBoostRow(rule));

        foreach (var app in settings.RecordingApps)
            RecordingApps.Add(CreateRecordingRow(app));

        AddMappingCommand = new RelayCommand(AddMapping);
        RemoveMappingCommand = new RelayCommand(RemoveMapping);
        AddChannelBoostCommand = new RelayCommand(AddChannelBoost);
        RemoveChannelBoostCommand = new RelayCommand(RemoveChannelBoost);
        AddRecordingAppCommand = new RelayCommand(AddRecordingApp);
        RemoveRecordingAppCommand = new RelayCommand(RemoveRecordingApp);
        RefreshCommand = new RelayCommand(async () => await RefreshAsync());

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.Activity += OnActivity;
        _coordinator.SettingsChanged += OnCoordinatorSettingsChanged;
        _routing.Activity += OnActivity;
        _defaultGuard.Activity += OnActivity;
        _defaultGuard.SupportChanged += OnDefaultGuardSupportChanged;
        _channelBoosts.Activity += OnActivity;
        _channelBoosts.RedirectionsChanged += OnRedirectionsChanged;
        _channelBoosts.VirtualDevicesChanged += OnVirtualDevicesChanged;
        _channelBoosts.ModeChanged += OnSonarModeChanged;
        _channelBoosts.SettingsChanged += OnBoostSettingsChanged;
        _chatMix.Activity += OnActivity;
        _chatMix.AvailabilityChanged += OnChatMixAvailabilityChanged;
        _deviceWatcher.DevicesChanged += OnAudioDevicesChanged;
    }

    public ObservableCollection<MappingRowViewModel> Mappings { get; } = [];

    /// <summary>"My Presets" — every Game-channel preset, favourites first.</summary>
    public ObservableCollection<SonarConfig> Presets { get; } = [];

    /// <summary>
    /// The same list for mapping rows, with "use the default from Settings" in front — the preset
    /// analogue of <see cref="MappingOutputDevices"/>.
    ///
    /// Kept separate from <see cref="Presets"/> because the Settings default picker is bound to that
    /// one, and a default that inherits from itself is meaningless.
    /// </summary>
    public ObservableCollection<SonarConfig> MappingPresets { get; } = [];

    /// <summary>Output devices for the global default picker: system default, then real devices.</summary>
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];

    /// <summary>Same list for mapping rows, with the "use the global default" entry in front.</summary>
    public ObservableCollection<AudioDevice> MappingOutputDevices { get; } = [];

    /// <summary>
    /// Recording devices for the per-mapping input picker — a different Windows enumeration from the
    /// render one above, so Sonar's virtual microphones appear here and nowhere else.
    /// </summary>
    public ObservableCollection<AudioDevice> RecordingInputDevices { get; } = [];

    /// <summary>Real devices only — "system default" is meaningless when you are picking what it is.</summary>
    public ObservableCollection<AudioDevice> SystemDefaultDevices { get; } = [];

    /// <summary>Per-device Sonar Volume Boost rules.</summary>
    public ObservableCollection<ChannelBoostRowViewModel> ChannelBoosts { get; } = [];

    /// <summary>Real hardware only — Sonar's own virtual endpoints are excluded from this one.</summary>
    public ObservableCollection<AudioDevice> ChannelBoostDevices { get; } = [];

    /// <summary>Recording and streaming apps, with their microphone and streamer-mode choice.</summary>
    public ObservableCollection<RecordingAppRowViewModel> RecordingApps { get; } = [];

    public ObservableCollection<string> ActivityLog { get; } = [];

    public RelayCommand AddMappingCommand { get; }

    public RelayCommand RemoveMappingCommand { get; }

    public RelayCommand AddChannelBoostCommand { get; }

    public RelayCommand RemoveChannelBoostCommand { get; }

    public RelayCommand AddRecordingAppCommand { get; }

    public RelayCommand RemoveRecordingAppCommand { get; }

    public RelayCommand RefreshCommand { get; }

    /// <summary>False when Windows will not expose the per-app routing API; the pickers grey out.</summary>
    public bool AudioRoutingSupported => _routing.IsSupported;

    public string OutputDeviceTooltip => AudioRoutingSupported
        ? "Sends this app's audio to a specific output device, exactly like Settings → Sound → " +
          "Volume mixer. The Windows default device is left alone.\n\n" +
          "Windows remembers the choice per executable, so it also applies the next time the game " +
          "launches — and a game that is already playing audio may need a restart to move over."
        : "Per-app audio routing is not available on this build of Windows.";

    public string InputDeviceTooltip => AudioRoutingSupported
        ? "For recording and streaming apps — OBS, Discord, a voice recorder. Sets the microphone the " +
          "app captures from, which is the input half of Settings → Sound → Volume mixer. Point one at " +
          "\"SteelSeries Sonar - Stream\" here and it picks that up the next time it launches.\n\n" +
          "This is not a game's in-game mic. It overrides what Windows hands back when an app asks for " +
          "the default microphone, so it does nothing for an app whose own audio settings name a " +
          "device — set those to \"Default\" first. It also leaves the Windows communications default " +
          "alone, which is the one most in-game voice chat asks for.\n\n" +
          "\"Don't change\" is the default and means exactly that: the app's input is left alone, " +
          "including anything you set by hand. Pick \"None — system default\" to actively clear an " +
          "override.\n\n" +
          "An app that is already recording has to be restarted to move over."
        : "Per-app audio routing is not available on this build of Windows.";

    public string RecordingAppsTooltip =>
        "Points recording and streaming apps — OBS, Discord, a voice recorder — at a microphone, and " +
        "optionally puts Sonar into streamer mode while they run.\n\n" +
        "Neither half touches a game's in-game microphone: the device is an override of the Windows " +
        "*default* mic, so an app whose own audio settings name a device ignores it.\n\n" +
        "Off by default. With this unticked nothing is written at all — no device override, and no " +
        "mode switch.";

    public string StreamerModeTooltip =>
        "Switches Sonar into streamer mode while this app is running, and back to the mode it was in " +
        "once it exits.\n\n" +
        "Streamer mode splits every channel into a Monitoring mix (what you hear) and a Streaming mix " +
        "(what goes out), which is what a capture app needs — but it changes Sonar's whole mixer, not " +
        "just this app's audio.\n\n" +
        "Only a mode this app switched away from is ever restored: if you were already in streamer " +
        "mode when the app launched, nothing is recorded and nothing is put back. The record survives " +
        "a restart, so being killed mid-stream still leaves the mixer where you had it.";

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

    public bool HasChannelBoosts => ChannelBoosts.Count > 0;

    public bool HasRecordingApps => RecordingApps.Count > 0;

    /// <summary>
    /// Sonar's mixer mode for the status card, or "" while it is unknown — the chip hides itself then,
    /// rather than claiming a mode the app has not actually read.
    /// </summary>
    public string SonarModeText =>
        _channelBoosts.LastMode is { } mode ? $"{SonarMode.Describe(mode)} mode" : "";

    public string SonarModeTooltip => _channelBoosts.LastMode switch
    {
        null => "Waiting for Sonar — the mixer mode is not known yet.",
        var m when SonarMode.Is(m, SonarMode.Classic) =>
            "Sonar is in classic mode: each channel plays straight out to its own Windows device.\n\n" +
            "Per-device volume boost rules match that device.",
        var m when SonarMode.Is(m, SonarMode.Streamer) =>
            "Sonar is in streamer mode: every channel is folded into a Monitoring mix (what you hear) " +
            "and a Streaming mix (what goes out), each with its own output device.\n\n" +
            "Per-device volume boost rules match the Monitoring device, since that is the one you are " +
            "listening on.",
        var m => $"Sonar reports a mode this app does not recognise (\"{m}\"), so the per-device volume " +
                 "boost is paused rather than guessing where the audio is going.",
    };

    public string ChannelBoostTooltip =>
        "Sonar's Volume Boost (0-12 dB) is stored per preset, not per device, so a level that suits " +
        "speakers is wrong the moment the channel moves to a headset.\n\n" +
        "Each row saves a boost for one output device. When Sonar starts playing that channel " +
        "through it, the boost is applied — once, on the change, so you can still move the slider " +
        "yourself afterwards. Leave a box empty to leave that channel alone.\n\n" +
        "Because Sonar has no per-device setting for this, the boost is written into whichever preset " +
        "is selected on that channel at the time, and that edit is permanent. Switching presets loads " +
        "a different boost, so it is re-applied after every switch.\n\n" +
        "Works in both of Sonar's mixer modes. In classic mode it follows the device the channel " +
        "plays out of; in streamer mode it follows the Monitoring mix — the one you hear — and does " +
        "nothing while stream monitoring is switched off.";

    public string RestoreBoostTooltip =>
        "On its own, a boost stays in the preset once applied — unticking the feature above, or moving " +
        "the channel to a device with no rule, leaves the last boost in place.\n\n" +
        "Tick this to put those presets back to the level on the right instead. 0 dB is Sonar's own " +
        "default.\n\n" +
        "Only presets this app actually boosted are touched; a Volume Boost you set yourself in GG is " +
        "never reset. The record survives a restart, so unticking the feature tomorrow still undoes " +
        "what was applied today.";

    /// <summary>False until Sonar has reported ChatMix as available; the slider and box grey out.</summary>
    public bool ChatMixSupported => _chatMix.IsSupported;

    public string ChatMixTooltip => ChatMixSupported
        ? "ChatMix balances the game and chat audio streams — centred is full volume of both, and " +
          "moving towards one end turns the other down.\n\n" +
          "It normally lives on the headset dial, and GG has no setting for where it should start, so " +
          "a GG restart or a stray bump of the dial leaves it wherever it landed. This puts your " +
          "balance back once, each time the app connects to Sonar.\n\n" +
          "It is not held: turn the dial afterwards and it stays where you put it until the next time " +
          "Sonar reconnects."
        : _chatMix.LastState is null
            ? "Waiting for Sonar — ChatMix availability is not known yet."
            : $"ChatMix is not available on this setup (Sonar reports \"{_chatMix.LastState}\"). " +
              "It needs a source with separate game and chat audio inputs.";

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

    public bool ApplyRecordingApps
    {
        get => _settings.ApplyRecordingApps;
        set
        {
            if (_settings.ApplyRecordingApps == value)
                return;

            _settings.ApplyRecordingApps = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool ApplyChannelBoost
    {
        get => _settings.ApplyChannelBoost;
        set
        {
            if (_settings.ApplyChannelBoost == value)
                return;

            _settings.ApplyChannelBoost = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool RestoreBoostOnRelease
    {
        get => _settings.RestoreBoostOnRelease;
        set
        {
            if (_settings.RestoreBoostOnRelease == value)
                return;

            _settings.RestoreBoostOnRelease = value;
            OnPropertyChanged();
            Persist();
        }
    }

    /// <summary>The level a released preset goes back to, as text so the box can be edited freely.</summary>
    public string RestoreBoostText
    {
        get => _settings.RestoreBoostDb.ToString("0.#", CultureInfo.CurrentCulture);
        set
        {
            var parsed = ParseDb(value) ?? 0;

            if (Math.Abs(_settings.RestoreBoostDb - parsed) > 0.001)
            {
                _settings.RestoreBoostDb = parsed;
                Persist();
            }

            // Raised either way, so a clamped or rejected entry snaps back to what was saved.
            OnPropertyChanged();
        }
    }

    private static double? ParseDb(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return null;

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return ChannelBoostRule.Clamp(value);

        return null;
    }

    public bool ApplyChatMix
    {
        get => _settings.ApplyChatMix;
        set
        {
            if (_settings.ApplyChatMix == value)
                return;

            _settings.ApplyChatMix = value;
            OnPropertyChanged();
            Persist();
        }
    }

    /// <summary>-1 full game, 1 full chat, 0 centred. Bound to the slider with a delay, so a drag
    /// saves once rather than at every pixel.</summary>
    public double ChatMixBalance
    {
        get => _settings.ChatMixBalance;
        set
        {
            var clamped = SonarChatMix.Clamp(value);
            if (_settings.ChatMixBalance == clamped)
                return;

            _settings.ChatMixBalance = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChatMixBalanceText));
            Persist();
        }
    }

    public string ChatMixBalanceText => SonarChatMix.Describe(_settings.ChatMixBalance);

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

            MappingPresets.Clear();
            MappingPresets.Add(SonarConfig.Inherit);
            foreach (var preset in presets)
                MappingPresets.Add(preset);

            var forRows = MappingPresets.ToList();
            foreach (var row in Mappings)
                row.SyncPreset(forRows);

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

        // Enumerated here rather than pushed in: the watcher follows render endpoints, and rebuilding
        // both halves in one pass keeps the repair below to a single Persist.
        var captureDevices = _audioDevices.GetCaptureDevices();
        var captureKnown = captureDevices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        var repaired = RepairSavedDeviceReferences(devices, known, captureDevices, captureKnown);

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

            // "Don't change" leads, so a row whose device has gone missing falls back to doing nothing
            // rather than to clearing an override the user may have set by hand.
            RecordingInputDevices.Clear();
            RecordingInputDevices.Add(AudioDevice.Unmanaged);
            RecordingInputDevices.Add(AudioDevice.SystemDefault);
            foreach (var device in WithMissing(captureDevices, captureKnown, RecordingInputReferences()))
                RecordingInputDevices.Add(device);

            SystemDefaultDevices.Clear();
            foreach (var device in WithMissing(devices, known, [KeepWindowsDefaultReference()]))
                SystemDefaultDevices.Add(device);

            // Sonar's own virtual endpoints are excluded here and nowhere else: boosting the channel
            // that feeds "Sonar - Gaming" is circular, whereas routing an app *to* it, or holding it as
            // the Windows default, are both things someone might legitimately want.
            ChannelBoostDevices.Clear();
            foreach (var device in WithMissing(devices, known, ChannelBoostReferences()))
            {
                if (!IsSonarVirtual(device))
                    ChannelBoostDevices.Add(device);
            }

            foreach (var row in Mappings)
                row.SyncOutputDevice(MappingOutputDevices);

            foreach (var row in RecordingApps)
                row.SyncInputDevice(RecordingInputDevices);

            foreach (var row in ChannelBoosts)
                row.SyncDevice(ChannelBoostDevices);

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
            if (string.IsNullOrEmpty(id) || id == AudioDevice.InheritId || id == AudioDevice.UnmanagedId ||
                known.ContainsKey(id))
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
        IReadOnlyList<AudioDevice> devices,
        IReadOnlyDictionary<string, AudioDevice> known,
        IReadOnlyList<AudioDevice> captureDevices,
        IReadOnlyDictionary<string, AudioDevice> captureKnown)
    {
        // Parameterised over the enumeration because render and capture ids live in separate namespaces:
        // repairing an input reference against the render list would match nothing, or worse, the wrong
        // endpoint that happens to share a friendly name with a playback device.
        (string Id, string Name) Repair(
            string id,
            string name,
            IReadOnlyList<AudioDevice> pool,
            IReadOnlyDictionary<string, AudioDevice> index)
        {
            var cleaned = AudioDevice.CleanName(name);

            if (string.IsNullOrEmpty(id) || id == AudioDevice.InheritId || id == AudioDevice.UnmanagedId)
                return (id, cleaned);

            if (!index.TryGetValue(id, out var live))
            {
                var moved = AudioDeviceService.FindUniqueId(pool, cleaned);
                if (moved is null)
                    return (id, cleaned);

                id = moved;
                live = index[moved];
            }

            return (id, live.Name);
        }

        (string Id, string Name) RepairRender(string id, string name) => Repair(id, name, devices, known);

        var changed = false;

        var (defaultId, defaultName) = RepairRender(_settings.DefaultOutputDeviceId, _settings.DefaultOutputDeviceName);
        if (defaultId != _settings.DefaultOutputDeviceId || defaultName != _settings.DefaultOutputDeviceName)
        {
            _settings.DefaultOutputDeviceId = defaultId;
            _settings.DefaultOutputDeviceName = defaultName;
            changed = true;
        }

        var (holdId, holdName) = RepairRender(_settings.KeepWindowsDefaultDeviceId, _settings.KeepWindowsDefaultDeviceName);
        if (holdId != _settings.KeepWindowsDefaultDeviceId || holdName != _settings.KeepWindowsDefaultDeviceName)
        {
            _settings.KeepWindowsDefaultDeviceId = holdId;
            _settings.KeepWindowsDefaultDeviceName = holdName;
            changed = true;
        }

        foreach (var mapping in _settings.Mappings)
        {
            var (id, name) = RepairRender(mapping.OutputDeviceId, mapping.OutputDeviceName);
            if (id == mapping.OutputDeviceId && name == mapping.OutputDeviceName)
                continue;

            mapping.OutputDeviceId = id;
            mapping.OutputDeviceName = name;
            changed = true;
        }

        // Against the capture pool. Sonar rebuilds its virtual microphone under a new id just as it does
        // the playback endpoints, so this is the same recovery, not a nicety.
        foreach (var app in _settings.RecordingApps)
        {
            var (id, name) = Repair(
                app.InputDeviceId, app.InputDeviceName, captureDevices, captureKnown);

            if (id == app.InputDeviceId && name == app.InputDeviceName)
                continue;

            app.InputDeviceId = id;
            app.InputDeviceName = name;
            changed = true;
        }

        foreach (var rule in _settings.ChannelBoosts)
        {
            var (id, name) = RepairRender(rule.DeviceId, rule.DeviceName);
            if (id == rule.DeviceId && name == rule.DeviceName)
                continue;

            rule.DeviceId = id;
            rule.DeviceName = name;
            changed = true;
        }

        return changed;
    }

    private (string Id, string Name) DefaultOutputReference() =>
        (_settings.DefaultOutputDeviceId, _settings.DefaultOutputDeviceName);

    private (string Id, string Name) KeepWindowsDefaultReference() =>
        (_settings.KeepWindowsDefaultDeviceId, _settings.KeepWindowsDefaultDeviceName);

    /// <summary>
    /// Sonar answers this authoritatively once it has been reached; the name match covers the window
    /// before that, and the case where Sonar is not running at all.
    /// </summary>
    private bool IsSonarVirtual(AudioDevice device) =>
        _channelBoosts.VirtualDeviceIds.Contains(device.Id) ||
        AudioDevice.LooksLikeSonarVirtual(device.Name);

    private IEnumerable<(string Id, string Name)> ChannelBoostReferences()
    {
        foreach (var rule in _settings.ChannelBoosts)
            yield return (rule.DeviceId, rule.DeviceName);
    }

    private IEnumerable<(string Id, string Name)> MappingReferences()
    {
        foreach (var mapping in _settings.Mappings)
        {
            if (!string.Equals(mapping.OutputDeviceId, AudioDevice.InheritId, StringComparison.Ordinal))
                yield return (mapping.OutputDeviceId, mapping.OutputDeviceName);
        }
    }

    private IEnumerable<(string Id, string Name)> RecordingInputReferences()
    {
        foreach (var app in _settings.RecordingApps)
        {
            if (!string.Equals(app.InputDeviceId, AudioDevice.UnmanagedId, StringComparison.Ordinal))
                yield return (app.InputDeviceId, app.InputDeviceName);
        }
    }

    /// <summary>Updates the green "running" dots. Called on a UI timer from the window.</summary>
    public void RefreshRunningFlags()
    {
        var running = _watcher.RunningProcesses;
        foreach (var row in Mappings)
            row.IsRunning = running.Contains(GameMapping.NormalizeProcessName(row.Mapping.ProcessName));

        foreach (var row in RecordingApps)
            row.IsRunning = running.Contains(GameMapping.NormalizeProcessName(row.App.ProcessName));
    }

    // ---- commands --------------------------------------------------------------------------

    private void AddMapping()
    {
        var dialog = new Views.AddMappingDialog(MappingPresets)
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
        row.SyncPreset(MappingPresets.ToList());
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

    /// <summary>
    /// Adds a rule for the first output device that does not have one yet. There is no dialog: the row
    /// carries its own device picker, and one device per rule is the only constraint.
    /// </summary>
    private void AddChannelBoost()
    {
        var taken = ChannelBoosts
            .Select(r => r.Rule.DeviceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var device = ChannelBoostDevices.FirstOrDefault(d => !taken.Contains(d.Id));
        if (device is null)
        {
            MessageBox.Show(
                Application.Current.MainWindow!,
                ChannelBoostDevices.Count == 0
                    ? "No output devices were found."
                    : "Every output device already has a boost rule.",
                "Nothing to add",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var rule = new ChannelBoostRule
        {
            DeviceId = device.Id,
            DeviceName = AudioDevice.CleanName(device.Name),
        };

        _settings.ChannelBoosts.Add(rule);
        var row = CreateBoostRow(rule);
        row.SyncDevice(ChannelBoostDevices);
        ChannelBoosts.Add(row);
        OnPropertyChanged(nameof(HasChannelBoosts));

        Log($"Added a volume boost rule for {device.DisplayName}.");
        Persist();
    }

    private void RemoveChannelBoost(object? parameter)
    {
        if (parameter is not ChannelBoostRowViewModel row)
            return;

        // Nothing to undo in Sonar: the levels this wrote are Sonar's own, and leaving them where they
        // are is what the user last heard.
        _settings.ChannelBoosts.Remove(row.Rule);
        ChannelBoosts.Remove(row);
        OnPropertyChanged(nameof(HasChannelBoosts));

        Log($"Removed the volume boost rule for {row.SelectedDevice?.DisplayName ?? row.Rule.DeviceName}.");
        Persist();
    }

    private void AddRecordingApp()
    {
        var dialog = new Views.AddMappingDialog(recordingApp: true)
        {
            Owner = Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        var picked = dialog.Result;

        var existing = RecordingApps.FirstOrDefault(r =>
            string.Equals(r.App.ProcessName, picked.ProcessName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            MessageBox.Show(
                Application.Current.MainWindow!,
                $"{picked.ProcessName}.exe is already in the recording apps list.",
                "Already added",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var device = DefaultRecordingDevice();
        var app = new RecordingApp
        {
            ProcessName = picked.ProcessName,
            DisplayName = picked.DisplayName,
            InputDeviceId = device.Id,
            InputDeviceName = device.IsSentinel ? "" : device.Name,
        };

        _settings.RecordingApps.Add(app);
        var row = CreateRecordingRow(app);
        row.SyncInputDevice(RecordingInputDevices);
        RecordingApps.Add(row);
        OnPropertyChanged(nameof(HasRecordingApps));

        Log($"Added {app.DisplayName} ({app.ProcessName}.exe), recording from \"{device.DisplayName}\".");
        RefreshRunningFlags();
        Persist();
    }

    private void RemoveRecordingApp(object? parameter)
    {
        if (parameter is not RecordingAppRowViewModel row)
            return;

        // The Windows override this wrote is deliberately left in place, exactly as when an app exits:
        // it is the same thing the Volume mixer would have written, and silently clearing it would be a
        // surprise. Removing the row only stops us managing it.
        _settings.RecordingApps.Remove(row.App);
        RecordingApps.Remove(row);
        OnPropertyChanged(nameof(HasRecordingApps));

        Log($"Removed {row.DisplayName} from the recording apps list.");
        Persist();
    }

    /// <summary>
    /// What a newly added recording app records from: Sonar's stream channel where it exists, then its
    /// virtual microphone, then nothing at all.
    ///
    /// Resolved when a row is *added* and never on a sync, so it can only ever fill a blank — it must
    /// not be able to overwrite a device the user picked. Sonar only creates the stream endpoint once
    /// streamer mode has been set up with an output, which is why this is a search rather than a
    /// constant: the day GG creates it, new rows pick it up with no code change.
    /// </summary>
    private AudioDevice DefaultRecordingDevice()
    {
        var real = RecordingInputDevices.Where(d => !d.IsSentinel).ToList();

        return real.FirstOrDefault(d => d.Name.Contains("Stream", StringComparison.OrdinalIgnoreCase))
            ?? real.FirstOrDefault(IsSonarVirtual)
            ?? AudioDevice.Unmanaged;
    }

    private MappingRowViewModel CreateRow(GameMapping mapping) => new(mapping, Persist);

    private ChannelBoostRowViewModel CreateBoostRow(ChannelBoostRule rule) => new(rule, Persist);

    private RecordingAppRowViewModel CreateRecordingRow(RecordingApp app) => new(app, Persist);

    private void Persist()
    {
        SettingsStore.Save(_settings);
        _coordinator.UpdateSettings(_settings);
        _defaultGuard.UpdateSettings(_settings);
        _channelBoosts.UpdateSettings(_settings);
        _chatMix.UpdateSettings(_settings);
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
    /// Windows has gained or lost a playback endpoint. Rebuild the pickers from the list the watcher
    /// already enumerated — silently, because the pickers changing is the whole message and a headset
    /// that sleeps and wakes should not fill the activity log.
    /// </summary>
    private void OnAudioDevicesChanged(IReadOnlyList<AudioDevice> devices) =>
        Dispatch(() => ApplyOutputDevices(devices));

    /// <summary>
    /// Sonar has told us which endpoints are its own. Rebuild the pickers so any virtual device that
    /// slipped past the name match before Sonar was reachable drops out of the boost list.
    /// </summary>
    private void OnVirtualDevicesChanged() =>
        Dispatch(() => ApplyOutputDevices(_audioDevices.GetRenderDevices()));

    /// <summary>
    /// The boost service has recorded, or cleared, a preset it changed. Saving is the view model's job,
    /// and it has to happen on the UI thread — the service raises this once per tick, after it has
    /// finished touching the list, so serialising it here cannot collide with a mutation.
    /// </summary>
    private void OnBoostSettingsChanged() => Dispatch(Persist);

    /// <summary>Lights the "in use" marker on whichever rules Sonar is currently playing through.</summary>
    private void OnRedirectionsChanged(IReadOnlyDictionary<string, string> redirections) => Dispatch(() =>
    {
        foreach (var row in ChannelBoosts)
            row.SyncRedirections(redirections);
    });

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

    /// <summary>
    /// Whether this setup has ChatMix at all is only known once Sonar has answered, which is after the
    /// window has bound — and it changes again if the headset is unplugged. Same re-read as the guard's.
    /// </summary>
    private void OnChatMixAvailabilityChanged() => Dispatch(() =>
    {
        OnPropertyChanged(nameof(ChatMixSupported));
        OnPropertyChanged(nameof(ChatMixTooltip));
    });

    /// <summary>
    /// The coordinator has recorded, or cleared, the mode it switched Sonar away from.
    ///
    /// Saved and nothing more. The full <see cref="Persist"/> would call back into the coordinator's
    /// <see cref="PresetCoordinator.UpdateSettings"/> and re-enter the apply it was raised from, and no
    /// other service reads the field, so there is nothing to fan out.
    /// </summary>
    private void OnCoordinatorSettingsChanged() => Dispatch(() => SettingsStore.Save(_settings));

    /// <summary>
    /// The mode chip in the status card. Sonar can be switched between Classic and Streamer while the
    /// app is running, so this is pushed rather than read once.
    /// </summary>
    private void OnSonarModeChanged(string? _) => Dispatch(() =>
    {
        OnPropertyChanged(nameof(SonarModeText));
        OnPropertyChanged(nameof(SonarModeTooltip));
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
