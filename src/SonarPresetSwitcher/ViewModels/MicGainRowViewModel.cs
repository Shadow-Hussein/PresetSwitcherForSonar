using System.Globalization;
using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.ViewModels;

/// <summary>
/// One row in the microphone gain list: "this much gain on Sonar's microphone while Sonar is reading
/// from that physical mic". Wraps a <see cref="MicGainRule"/> and adds the live "Sonar is using this
/// microphone" state — the capture-side twin of <see cref="ChannelBoostRowViewModel"/>.
/// </summary>
public sealed class MicGainRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private AudioDevice? _selectedDevice;
    private bool _isInUse;
    private bool _suppressNotifications;

    public MicGainRowViewModel(MicGainRule rule, Action onChanged)
    {
        Rule = rule;
        _onChanged = onChanged;
    }

    public MicGainRule Rule { get; }

    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value)
                return;

            Rule.Enabled = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    /// <summary>
    /// The physical microphone this rule is for. Real endpoints only — never a Sonar one: the gain is
    /// always applied on Sonar's microphone, and this only says when.
    /// </summary>
    public AudioDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetField(ref _selectedDevice, value) || value is null)
                return;

            Rule.DeviceId = value.Id;
            Rule.DeviceName = AudioDevice.CleanName(value.Name);

            if (!_suppressNotifications)
                _onChanged();
        }
    }

    /// <summary>
    /// Text rather than a slider to match the volume boost rows beside it. Anything that is not a
    /// number reads as 0, and the property is re-raised either way so a clamped or rejected entry
    /// snaps back to what was saved.
    /// </summary>
    public string GainText
    {
        get => Rule.GainDb.ToString("0.#", CultureInfo.CurrentCulture);
        set
        {
            var parsed = Parse(value);

            if (Rule.GainDb != parsed)
            {
                Rule.GainDb = parsed;
                _onChanged();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>True while Sonar's microphone input is this device, which is when the rule applies.</summary>
    public bool IsInUse
    {
        get => _isInUse;
        private set
        {
            if (!SetField(ref _isInUse, value))
                return;

            OnPropertyChanged(nameof(InUseTooltip));
        }
    }

    public string InUseTooltip => IsInUse
        ? "Sonar is using this microphone right now, so this row's gain is the one applied."
        : "Sonar is not using this microphone right now. The gain applies when it is selected as the " +
          "microphone in Sonar.";

    /// <summary>
    /// Re-points <see cref="SelectedDevice"/> at the matching entry in a freshly loaded device list
    /// without treating it as a user edit — the counterpart of
    /// <see cref="ChannelBoostRowViewModel.SyncDevice"/>.
    /// </summary>
    public void SyncDevice(IReadOnlyList<AudioDevice> devices)
    {
        var match = devices.FirstOrDefault(d =>
            string.Equals(d.Id, Rule.DeviceId, StringComparison.OrdinalIgnoreCase));

        _suppressNotifications = true;
        try
        {
            SelectedDevice = match;
        }
        finally
        {
            _suppressNotifications = false;
        }
    }

    /// <summary>Updates the "in use" marker from the microphone Sonar is currently reading.</summary>
    public void SyncSonarMic(string? sonarInputId) =>
        IsInUse = !string.IsNullOrEmpty(sonarInputId) &&
                  !string.IsNullOrEmpty(Rule.DeviceId) &&
                  string.Equals(sonarInputId, Rule.DeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Current culture first, then invariant, so both "7,5" and a pasted "7.5" work.</summary>
    private static double Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return MicGainRule.Clamp(value);

        return 0;
    }
}
