using System.Globalization;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.ViewModels;

/// <summary>
/// One row in the per-device volume boost list. Wraps a <see cref="ChannelBoostRule"/> and adds the
/// live "Sonar is using this device" state.
///
/// The two levels are edited as text rather than through a slider because an empty box is a meaningful
/// value here — it means "leave that channel alone" — and a slider has no way to say it.
/// </summary>
public sealed class ChannelBoostRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private AudioDevice? _selectedDevice;
    private bool _isInUseByGame;
    private bool _isInUseByMedia;
    private bool _suppressNotifications;

    public ChannelBoostRowViewModel(ChannelBoostRule rule, Action onChanged)
    {
        Rule = rule;
        _onChanged = onChanged;
    }

    public ChannelBoostRule Rule { get; }

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

    /// <summary>The output device this rule applies to. Real endpoints only — never a Sonar one.</summary>
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

    public string GameBoostText
    {
        get => Format(Rule.GameBoostDb);
        set => SetBoost(value, () => Rule.GameBoostDb, v => Rule.GameBoostDb = v, nameof(GameBoostText));
    }

    public string MediaBoostText
    {
        get => Format(Rule.MediaBoostDb);
        set => SetBoost(value, () => Rule.MediaBoostDb, v => Rule.MediaBoostDb = v, nameof(MediaBoostText));
    }

    /// <summary>True while Sonar is playing the Game channel out of this device.</summary>
    public bool IsInUseByGame
    {
        get => _isInUseByGame;
        set
        {
            if (SetField(ref _isInUseByGame, value))
                RaiseInUse();
        }
    }

    /// <summary>True while Sonar is playing the Media channel out of this device.</summary>
    public bool IsInUseByMedia
    {
        get => _isInUseByMedia;
        set
        {
            if (SetField(ref _isInUseByMedia, value))
                RaiseInUse();
        }
    }

    public bool IsInUse => IsInUseByGame || IsInUseByMedia;

    public string InUseTooltip => (IsInUseByGame, IsInUseByMedia) switch
    {
        (true, true) => "Sonar is playing both the Game and Media channels through this device.",
        (true, false) => "Sonar is playing the Game channel through this device.",
        (false, true) => "Sonar is playing the Media channel through this device.",
        _ => "Sonar is not playing either channel through this device right now.",
    };

    /// <summary>
    /// Re-points <see cref="SelectedDevice"/> at the matching entry in a freshly loaded device list
    /// without treating it as a user edit — the counterpart of
    /// <see cref="MappingRowViewModel.SyncOutputDevice"/>.
    /// </summary>
    public void SyncDevice(IReadOnlyList<AudioDevice> devices)
    {
        var match = devices.FirstOrDefault(d =>
            string.Equals(d.Id, Rule.DeviceId, StringComparison.OrdinalIgnoreCase));

        Suppressed(() => SelectedDevice = match);
    }

    /// <summary>Updates the "in use" marker from Sonar's current channel redirections.</summary>
    public void SyncRedirections(IReadOnlyDictionary<string, string> redirections)
    {
        IsInUseByGame = Matches(redirections, SonarRedirection.GameChannel);
        IsInUseByMedia = Matches(redirections, SonarRedirection.MediaChannel);
    }

    private bool Matches(IReadOnlyDictionary<string, string> redirections, string channel) =>
        redirections.TryGetValue(channel, out var deviceId) &&
        !string.IsNullOrEmpty(Rule.DeviceId) &&
        string.Equals(deviceId, Rule.DeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses one of the two boxes. Anything that is not a number — an empty box included — means
    /// "leave this channel alone"; a number is clamped to 0-12 dB and onto Sonar's 0.1 step. The
    /// property is re-raised either way, so a clamped or rejected entry is reflected straight back into
    /// the box instead of sitting there disagreeing with what was saved.
    /// </summary>
    private void SetBoost(string? text, Func<double?> get, Action<double?> set, string propertyName)
    {
        var parsed = Parse(text);

        if (get() != parsed)
        {
            set(parsed);

            if (!_suppressNotifications)
                _onChanged();
        }

        OnPropertyChanged(propertyName);
    }

    /// <summary>
    /// Accepts the current culture first so a comma decimal separator works where Windows expects one,
    /// then falls back to invariant so a pasted "7.5" is not silently rejected either.
    /// </summary>
    private static double? Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return null;

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return ChannelBoostRule.Clamp(value);

        return null;
    }

    private static string Format(double? db) =>
        db is null ? "" : db.Value.ToString("0.#", CultureInfo.CurrentCulture);

    private void RaiseInUse()
    {
        OnPropertyChanged(nameof(IsInUse));
        OnPropertyChanged(nameof(InUseTooltip));
    }

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
