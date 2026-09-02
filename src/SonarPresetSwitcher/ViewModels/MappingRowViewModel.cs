using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.ViewModels;

/// <summary>One row in the mappings list. Wraps a <see cref="GameMapping"/> and adds live UI state.</summary>
public sealed class MappingRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private SonarConfig? _selectedPreset;
    private AudioDevice? _selectedOutputDevice;
    private bool _isRunning;
    private bool _presetMissing;
    private bool _suppressNotifications;

    public MappingRowViewModel(GameMapping mapping, Action onChanged)
    {
        Mapping = mapping;
        _onChanged = onChanged;
    }

    public GameMapping Mapping { get; }

    public string DisplayName => Mapping.DisplayName;

    public string ProcessLabel => Mapping.ProcessName + ".exe";

    public bool Enabled
    {
        get => Mapping.Enabled;
        set
        {
            if (Mapping.Enabled == value)
                return;

            Mapping.Enabled = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public SonarConfig? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value) || value is null)
                return;

            Mapping.PresetId = value.Id;
            Mapping.PresetName = value.Name;
            PresetMissing = false;

            if (!_suppressNotifications)
                _onChanged();
        }
    }

    /// <summary>Windows output device for this game, including the two sentinel entries.</summary>
    public AudioDevice? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetField(ref _selectedOutputDevice, value) || value is null)
                return;

            Mapping.OutputDeviceId = value.Id;
            Mapping.OutputDeviceName = value.IsSentinel ? "" : value.Name;

            if (!_suppressNotifications)
                _onChanged();
        }
    }

    /// <summary>
    /// <summary>True while the mapped executable is in the process table.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    /// <summary>True when the saved preset is no longer among Sonar's Game presets.</summary>
    public bool PresetMissing
    {
        get => _presetMissing;
        set
        {
            if (SetField(ref _presetMissing, value))
                OnPropertyChanged(nameof(MissingPresetTooltip));
        }
    }

    public string MissingPresetTooltip =>
        $"\"{Mapping.PresetName}\" is no longer on the Sonar Game channel — it was probably deleted or " +
        "renamed in SteelSeries GG. Pick a different preset here.";

    /// <summary>
    /// Re-points <see cref="SelectedPreset"/> at the matching entry in a freshly loaded preset list
    /// without treating it as a user edit.
    /// </summary>
    public void SyncPreset(IReadOnlyList<SonarConfig> presets)
    {
        var match = presets.FirstOrDefault(p =>
            string.Equals(p.Id, Mapping.PresetId, StringComparison.OrdinalIgnoreCase));

        Suppressed(() => SelectedPreset = match);

        PresetMissing = match is null && !string.IsNullOrWhiteSpace(Mapping.PresetId);
    }

    /// <summary>Same idea for the output-device picker.</summary>
    public void SyncOutputDevice(IReadOnlyList<AudioDevice> devices)
    {
        var match = devices.FirstOrDefault(d =>
            string.Equals(d.Id, Mapping.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Id == AudioDevice.InheritId);

        Suppressed(() => SelectedOutputDevice = match);
    }

    /// <summary>
    private void Suppressed(Action action)
    {
        _suppressNotifications = true;
        try
        {
            action();
        }
        finally
        {
            _suppressNotifications = false;
        }
    }
}
