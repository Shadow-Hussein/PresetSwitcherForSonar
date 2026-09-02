using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.ViewModels;

/// <summary>
/// One row in the recording apps list. Wraps a <see cref="RecordingApp"/> and adds the live
/// "this app is running" state.
///
/// The counterpart of <see cref="MappingRowViewModel"/> for capture apps, and deliberately not the
/// same class: a mapping row is about a preset and an output device, this one about a microphone and
/// Sonar's mixer mode.
/// </summary>
public sealed class RecordingAppRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private AudioDevice? _selectedInputDevice;
    private bool _isRunning;
    private bool _suppressNotifications;

    public RecordingAppRowViewModel(RecordingApp app, Action onChanged)
    {
        App = app;
        _onChanged = onChanged;
    }

    public RecordingApp App { get; }

    public string DisplayName => App.DisplayName;

    public string ProcessLabel => App.ProcessName + ".exe";

    public bool Enabled
    {
        get => App.Enabled;
        set
        {
            if (App.Enabled == value)
                return;

            App.Enabled = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    /// <summary>
    /// The microphone this app records from. <see cref="AudioDevice.Unmanaged"/> leaves the input
    /// alone entirely.
    /// </summary>
    public AudioDevice? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (!SetField(ref _selectedInputDevice, value) || value is null)
                return;

            App.InputDeviceId = value.Id;
            App.InputDeviceName = value.IsSentinel ? "" : value.Name;

            if (!_suppressNotifications)
                _onChanged();
        }
    }

    /// <summary>
    /// Switches Sonar to streamer mode while this app runs, and back afterwards. Off by default — it
    /// changes Sonar's whole mixer, not just this app's audio.
    /// </summary>
    public bool RequireStreamerMode
    {
        get => App.RequireStreamerMode;
        set
        {
            if (App.RequireStreamerMode == value)
                return;

            App.RequireStreamerMode = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    /// <summary>True while the app's executable is in the process table.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    /// <summary>
    /// Re-points <see cref="SelectedInputDevice"/> at the matching entry in a freshly loaded device
    /// list without treating it as a user edit. Falls back to "Don't change" rather than to the system
    /// default, so a device that has gone missing never turns into an instruction to clear the
    /// override.
    /// </summary>
    public void SyncInputDevice(IReadOnlyList<AudioDevice> devices)
    {
        var match = devices.FirstOrDefault(d =>
            string.Equals(d.Id, App.InputDeviceId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Id == AudioDevice.UnmanagedId);

        _suppressNotifications = true;
        try
        {
            SelectedInputDevice = match;
        }
        finally
        {
            _suppressNotifications = false;
        }
    }
}
