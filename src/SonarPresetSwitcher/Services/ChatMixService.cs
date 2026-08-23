using System.Diagnostics;
using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.Services;

/// <summary>
/// Puts a chosen ChatMix balance back once each time this app reaches Sonar.
///
/// ChatMix is normally driven by a dial on the headset, and GG offers no way to say where it should
/// start. A GG restart, a headset reconnect or a stray bump of the dial leaves the balance wherever it
/// landed; this restores the value the user picked.
///
/// <b>Apply-once, not hold.</b> The write happens on the transition into "Sonar reachable" and never
/// again until that transition repeats, so turning the dial afterwards works exactly as it always did.
/// <see cref="_applied"/> is cleared the moment a poll fails, and clearing it is the entire mechanism —
/// the next successful poll re-arms the write. Holding the value instead would mean fighting the
/// hardware dial every few seconds, which is not what the feature is for.
///
/// Deliberately its own poller rather than part of <see cref="PresetCoordinator"/> or
/// <see cref="ChannelBoostService"/>: the coordinator is keyed on running processes and owns preset
/// selection, the boost service is keyed on Sonar's redirection state, and this is keyed on nothing but
/// the connection. Unlike the boost, nothing is persisted back — the balance is transient state on
/// Sonar, not an edit to a preset, so there is no record to keep and nothing to restore on release.
/// </summary>
public sealed class ChatMixService : IDisposable
{
    /// <summary>
    /// Sonar keeps the balance as a float, so a value put as 0.75 reads back as 0.75000006. Half of
    /// the 0.01 step the UI offers is comfortably wider than that.
    /// </summary>
    private const double BalanceEpsilon = 0.005;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);

    private readonly SonarClient _client;
    private readonly CancellationTokenSource _cts = new();

    private AppSettings _settings;
    private Task? _loop;
    private bool _reachable;

    /// <summary>
    /// Set once the balance has been applied for the current connection. Cleared on every failed poll,
    /// which is what makes this fire again after a GG restart. Only touched by the loop.
    /// </summary>
    private bool _applied;

    private bool _unsupportedAnnounced;
    private volatile bool _invalidated;

    public ChatMixService(SonarClient client, AppSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    /// <summary>Human-readable lines for the activity log.</summary>
    public event Action<string>? Activity;

    /// <summary>Raised when <see cref="IsSupported"/> or <see cref="LastState"/> has changed.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>
    /// Whether Sonar last reported ChatMix as available. False until Sonar has been reached once, so
    /// the UI has to re-read it on <see cref="AvailabilityChanged"/> rather than at construction —
    /// GG may not be up yet at logon.
    /// </summary>
    public bool IsSupported { get; private set; }

    /// <summary>
    /// The raw <c>state</c> of the last successful poll, or null if there has not been one. Kept
    /// verbatim because the set of values Sonar can report is undocumented.
    /// </summary>
    public string? LastState { get; private set; }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>Called when the user moves the slider or toggles the feature.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;

        // A new balance has to take effect now, not at the next reconnect. The loop clears its own
        // state rather than this thread touching it.
        _invalidated = true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ChatMix tick failed: {ex}");
            }

            try
            {
                await Task.Delay(_reachable ? PollInterval : ReconnectInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_invalidated)
        {
            _invalidated = false;
            _applied = false;
        }

        var settings = _settings;

        // The poll runs even with the feature off, so the UI can tell whether this machine has ChatMix
        // at all before the user ticks the box.
        var chatMix = await _client.GetChatMixAsync(ct).ConfigureAwait(false);
        if (chatMix is null)
        {
            _reachable = false;

            // Re-arms the write for the next connection. This is the apply-once mechanism.
            _applied = false;
            return;
        }

        _reachable = true;
        RaiseAvailability(chatMix);

        if (!chatMix.IsUsable)
        {
            if (!_unsupportedAnnounced)
            {
                _unsupportedAnnounced = true;
                Activity?.Invoke(
                    $"ChatMix is not available on this setup (Sonar reports \"{chatMix.State}\"). " +
                    "It needs a source with separate game and chat audio inputs.");
            }

            return;
        }

        _unsupportedAnnounced = false;

        if (!settings.ApplyChatMix || _applied)
            return;

        var target = SonarChatMix.Clamp(settings.ChatMixBalance);

        // Already there — latch without writing, so a no-op never reaches the activity log.
        if (Math.Abs(chatMix.Balance - target) < BalanceEpsilon)
        {
            _applied = true;
            return;
        }

        if (await _client.SetChatMixAsync(target, ct).ConfigureAwait(false))
            Activity?.Invoke($"ChatMix set to {SonarChatMix.Describe(target)}.");
        else
            Activity?.Invoke("Sonar refused the ChatMix request.");

        // Latched either way: a value Sonar rejects is not worth retrying every five seconds. The next
        // reconnect, or an edit in the UI, arms it again.
        _applied = true;
    }

    private void RaiseAvailability(SonarChatMix chatMix)
    {
        if (IsSupported == chatMix.IsUsable && string.Equals(LastState, chatMix.State, StringComparison.Ordinal))
            return;

        IsSupported = chatMix.IsUsable;
        LastState = chatMix.State;
        AvailabilityChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
