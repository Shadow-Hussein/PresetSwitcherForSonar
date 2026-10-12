using System.Diagnostics;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// Holds the Windows default playback device at the one the user chose, putting it back whenever
/// something else moves it.
///
/// The "something else" this exists for is SteelSeries GG: it re-asserts its own virtual device
/// ("SteelSeries Sonar - Gaming") as the Windows default every time it starts, which on a normal boot
/// means the default the user set by hand is gone by the time they log in. GG has no setting to turn
/// that off, so the only fix is to change it back afterwards.
///
/// Design notes:
///
///   - It polls rather than registering an <c>IMMNotificationClient</c>. Every other COM call in this
///     app creates and releases an enumerator inside a single call, and a long-lived notification
///     callback would drag apartment and lifetime concerns into a service that otherwise has none.
///     Being wrong for a second at boot costs nothing, as long as it is recoverable.
///   - The read-back after a write is proof of success only, never of failure. The one thing that
///     condemns the API is the signature of a wrong vtable slot: Windows returned success for every
///     role and nothing moved, repeatedly. A default that moves and is moved back is a fight with
///     another app, and the answer to a fight is to keep going — an earlier version treated the two
///     the same and so killed itself at logon, in the middle of the very fight it was written for.
///     Once a write has been seen to land, <see cref="_apiProven"/> makes standing down impossible.
///   - Everything is checked more often for the first minute after startup, and again whenever a
///     SteelSeries process appears, because that is when the default actually moves.
///   - Turning the option off leaves the current default alone. The guard only ever writes the device
///     the user picked; it has no notion of "restore what was there before".
/// </summary>
public sealed class DefaultDeviceGuard : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>Cadence while the default is still in play: just after startup, or after GG appears.</summary>
    private static readonly TimeSpan BurstInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a burst lasts. Long enough to outlast GG finishing its own startup.</summary>
    private static readonly TimeSpan BurstDuration = TimeSpan.FromSeconds(60);

    /// <summary>Fallback cadence once a fight with another app is detected.</summary>
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(15);

    /// <summary>One log line per correction would be noise; this is the minimum gap between them.</summary>
    private static readonly TimeSpan AnnounceQuietPeriod = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ThrashWindow = TimeSpan.FromSeconds(60);
    private const int ThrashThreshold = 6;

    /// <summary>How many times the API may claim success and move nothing before it is disbelieved.</summary>
    private const int CleanWritesBeforeStandDown = 3;

    /// <summary>How long Windows may refuse outright before a build that cannot do this is accepted.</summary>
    private static readonly TimeSpan RefusalGrace = TimeSpan.FromMinutes(5);

    /// <summary>Every GG component is named "SteelSeries…"; the ones that matter are GG itself and Sonar.</summary>
    private const string SteelSeriesPrefix = "SteelSeries";

    private readonly AudioDeviceService _devices;
    private readonly ProcessWatcher _watcher;
    private readonly Lock _gate = new();
    private readonly Queue<DateTime> _corrections = new();
    private readonly Timer _timer;

    private AppSettings _settings;
    private int _busy;
    private bool _started;
    private bool _disposed;

    /// <summary>Set when a write verifiably did not take; stops the guard for the rest of the session.</summary>
    private bool _standDown;

    /// <summary>
    /// Set the first time a write is observed to move the default. Once true, standing down is
    /// impossible — the API demonstrably works, so every later disappointment is the world, not the
    /// vtable. Deliberately per-session and never persisted: a Windows update between runs is exactly
    /// what the read-back exists to catch.
    /// </summary>
    private bool _apiProven;

    private int _cleanWritesWithoutEffect;
    private DateTime? _refusingSince;
    private DateTime _burstUntil = DateTime.MinValue;
    private TimeSpan _currentInterval = Interval;

    /// <summary>Makes the next tick land sooner: a read-back was inconclusive and wants a second look.</summary>
    private bool _recheckSoon;

    private bool _supported;
    private bool _unavailableLogged;
    private bool _refusalLogged;
    private bool _targetMissingLogged;
    private bool _thrashWarned;
    private DateTime _lastAnnounce = DateTime.MinValue;

    /// <summary>
    /// What the guard last announced it was doing. <see cref="AppSettings"/> is a single instance
    /// shared by reference with the view model, so by the time <see cref="UpdateSettings"/> runs it
    /// already holds the new values — the previous state has to be remembered here or not at all.
    /// </summary>
    private bool _lastEnabled;

    private string _lastTarget = "";

    /// <summary>
    /// The saved id that stopped resolving, and the live endpoint carrying the same name that took its
    /// place. Empty until <see cref="Enforce"/> has had to look one up. See <see cref="CurrentTarget"/>.
    /// </summary>
    private string _resolvedFrom = "";

    private string _resolvedTo = "";

    public DefaultDeviceGuard(AudioDeviceService devices, ProcessWatcher watcher, AppSettings settings)
    {
        _devices = devices;
        _watcher = watcher;
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _watcher.Started += OnProcessesStarted;
    }

    /// <summary>Fired with a short human-readable line for the activity log.</summary>
    public event Action<string>? Activity;

    /// <summary>
    /// Fired when <see cref="IsSupported"/> becomes true after having been false, so the UI can stop
    /// greying the row out. Creating the underlying client can fail for the first seconds of a logon.
    /// </summary>
    public event Action? SupportChanged;

    /// <summary>False when this build of Windows will not let us move the default; the UI greys out.</summary>
    public bool IsSupported => _devices.CanSetDefaultDevice;

    public void Start()
    {
        AppSettings settings;

        lock (_gate)
        {
            if (_disposed || _started)
                return;

            _started = true;
            settings = _settings;
            _lastEnabled = Enabled(settings);
            _lastTarget = settings.KeepWindowsDefaultDeviceId;
            _burstUntil = DateTime.UtcNow + BurstDuration;
            _currentInterval = BurstInterval;
        }

        if (Enabled(settings))
            Activity?.Invoke($"Holding the Windows default playback device at \"{TargetName(settings)}\".");

        // Whether the API is usable is decided on the timer thread by the first tick, never here: a
        // verdict reached at t=0 on the UI thread is both premature and, once it was allowed to skip
        // arming the timer, permanent.
        Schedule(BurstInterval, dueNow: true);
    }

    /// <summary>
    /// Swaps in the settings instance after the user changed something and re-arms: a new device, or
    /// simply re-ticking the box, is also how the user retries after the guard stood down.
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        var enabled = Enabled(settings);
        var target = settings.KeepWindowsDefaultDeviceId;

        bool wasEnabled;
        string lastTarget;

        lock (_gate)
        {
            wasEnabled = _lastEnabled;
            lastTarget = _lastTarget;

            _settings = settings;
            _lastEnabled = enabled;
            _lastTarget = target;

            // Every change is also the user's way of retrying after the guard stood down. _apiProven is
            // not reset: it records what Windows did, not what the user asked for.
            _standDown = false;
            _thrashWarned = false;
            _cleanWritesWithoutEffect = 0;
            _refusingSince = null;
            _refusalLogged = false;
            _targetMissingLogged = false;
            _resolvedFrom = "";
            _resolvedTo = "";
            _corrections.Clear();
            _lastAnnounce = DateTime.MinValue;
            _burstUntil = DateTime.UtcNow + BurstDuration;
            _currentInterval = BurstInterval;
        }

        if (!enabled)
        {
            if (wasEnabled)
                Activity?.Invoke("No longer holding the Windows default playback device.");

            return;
        }

        if (!wasEnabled || !string.Equals(target, lastTarget, StringComparison.OrdinalIgnoreCase))
            Activity?.Invoke($"Holding the Windows default playback device at \"{TargetName(settings)}\".");

        Schedule(BurstInterval, dueNow: true);
    }

    /// <summary>
    /// GG re-claims the default as it starts, so its appearance is the cue to watch closely for a
    /// while. This runs on the watcher's thread and must not touch COM.
    ///
    /// The watcher reports every process name, not just mapped ones, and its first tick reports
    /// everything already running — so this covers "GG was already up" and a restart after resume as
    /// well as a fresh launch. It stays a hint rather than the trigger: the poll interval is the user's
    /// to set, and pausing monitoring stops these events entirely, which is why <see cref="Start"/>
    /// arms a burst unconditionally.
    /// </summary>
    private void OnProcessesStarted(IReadOnlyCollection<string> names)
    {
        var sawSteelSeries = names.Any(n => n.StartsWith(SteelSeriesPrefix, StringComparison.OrdinalIgnoreCase));
        if (!sawSteelSeries)
            return;

        lock (_gate)
        {
            if (_disposed || !_started || _standDown || !Enabled(_settings))
                return;

            _burstUntil = DateTime.UtcNow + BurstDuration;
            _currentInterval = BurstInterval;
        }

        Schedule(BurstInterval, dueNow: true);
    }

    private void Schedule(TimeSpan period, bool dueNow)
    {
        lock (_gate)
        {
            if (_disposed || !_started)
                return;

            try
            {
                _timer.Change(dueNow ? TimeSpan.Zero : period, period);
            }
            catch (ObjectDisposedException)
            {
                // Raced with Dispose; nothing left to schedule.
            }
        }
    }

    private void Tick()
    {
        // A correction takes a few milliseconds, but a stalled COM call must not queue up ticks.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            EnsureSupport();
            Enforce();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DefaultDeviceGuard tick failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            ApplyCadence();
        }
    }

    /// <summary>
    /// Establishes — and keeps re-establishing, for a while — whether the default endpoint can be
    /// moved. Runs whether or not the option is on, so the checkbox stops being greyed out either way.
    /// </summary>
    private void EnsureSupport()
    {
        var supported = _devices.EnsureDefaultDeviceSupport();

        bool changed;
        bool announceUnavailable = false;

        lock (_gate)
        {
            changed = supported != _supported;
            _supported = supported;

            if (!supported && !_unavailableLogged && _devices.DefaultDeviceSupportSettled && Enabled(_settings))
            {
                _unavailableLogged = true;
                announceUnavailable = true;
            }
        }

        if (changed && supported)
            SupportChanged?.Invoke();

        if (announceUnavailable)
            Activity?.Invoke(
                "Cannot hold the Windows default playback device — the API is unavailable on this build of Windows.");
    }

    private void Enforce()
    {
        AppSettings settings;

        lock (_gate)
        {
            if (_disposed || _standDown || !_supported)
                return;

            settings = _settings;
        }

        if (!Enabled(settings))
            return;

        var target = CurrentTarget(settings);

        var before = _devices.GetDefaultRenderHold(target);
        if (before == DefaultDeviceHold.Both)
        {
            lock (_gate)
                _targetMissingLogged = false;

            return;
        }

        // Unplugged or disabled — or simply wearing a different id than the one that was saved. Sonar's
        // virtual endpoints are re-created with a new id whenever GG rebuilds its driver, and by id alone
        // that is indistinguishable from the device being gone, which is how a working setting turns into
        // a permanent "waiting for…". The saved name is the only thing that survives, so it decides.
        if (!_devices.IsRenderDeviceActive(target))
        {
            var moved = _devices.FindRenderDeviceIdByName(AudioDevice.CleanName(settings.KeepWindowsDefaultDeviceName));

            if (moved is null)
            {
                // Really not there. Leave Windows alone rather than pushing it at a dead endpoint; this
                // also covers the machine with no default at all, where nothing is active either.
                AnnounceTargetMissing(settings);
                return;
            }

            lock (_gate)
            {
                _resolvedFrom = settings.KeepWindowsDefaultDeviceId;
                _resolvedTo = moved;
                _targetMissingLogged = false;
            }

            Activity?.Invoke($"\"{TargetName(settings)}\" is back under a new device id — following it.");
            target = moved;
        }

        // Read only when a correction is actually due, so the log can name what took it. Costs one
        // enumerator per write attempt, not per tick.
        var takenBy = _devices.GetDefaultRenderDeviceId();

        var result = _devices.SetDefaultRenderDevice(target);
        var after = _devices.GetDefaultRenderHold(target);

        Classify(result, after, settings, takenBy);
    }

    /// <summary>
    /// The whole decision. <paramref name="after"/> is the only evidence that counts for success;
    /// <paramref name="result"/> only distinguishes the ways a failure can be innocent.
    /// </summary>
    private void Classify(
        DefaultDeviceWriteResult result, DefaultDeviceHold after, AppSettings settings, string? takenBy)
    {
        if (after != DefaultDeviceHold.None)
        {
            // Something moved, so the slot is the right one. A partial hold needs no log of its own:
            // the next tick sees it is not Both and writes the missing role again.
            lock (_gate)
            {
                _apiProven = true;
                _cleanWritesWithoutEffect = 0;
                _refusingSince = null;
                _recheckSoon = after == DefaultDeviceHold.Partial;
            }

            RecordCorrection(settings, takenBy);
            return;
        }

        switch (result)
        {
            case DefaultDeviceWriteResult.PartlyAccepted:
                // One role reported success even though the read-back shows nothing: the call is real,
                // and the read is either stale or already undone. Either way, look again shortly.
                lock (_gate)
                {
                    _apiProven = true;
                    _refusingSince = null;
                    _recheckSoon = true;
                }

                return;

            case DefaultDeviceWriteResult.Accepted:
                bool giveUp;

                lock (_gate)
                {
                    _refusingSince = null;
                    _recheckSoon = true;

                    // Success with no effect is the signature of a wrong vtable slot — but only if the
                    // API has never worked. Once it has, this is just GG having won the round.
                    giveUp = !_apiProven && ++_cleanWritesWithoutEffect >= CleanWritesBeforeStandDown;
                }

                if (giveUp)
                    StandDown(
                        "Windows accepted the change but the default playback device did not move — " +
                        "not trying again until the setting changes.");

                return;

            case DefaultDeviceWriteResult.Refused:
            case DefaultDeviceWriteResult.Unsupported:
            default:
                bool exhausted;
                bool announce;

                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    _refusingSince ??= now;
                    _recheckSoon = true;

                    // Refusals are ordinary at logon while the audio service is still starting.
                    exhausted = !_apiProven && now - _refusingSince.Value > RefusalGrace;
                    announce = exhausted && !_refusalLogged;

                    if (announce)
                        _refusalLogged = true;
                }

                if (exhausted)
                    StandDown(
                        "Windows will not change the default playback device on this build — " +
                        "not trying again until the setting changes.");

                return;
        }
    }

    /// <summary>Once a write has been seen to land, no evidence can justify giving up.</summary>
    private void StandDown(string reason)
    {
        lock (_gate)
        {
            if (_standDown || _apiProven)
                return;

            _standDown = true;
        }

        Activity?.Invoke(reason);
    }

    /// <summary>
    /// Says once per session that the held device is not there. Today that silent skip is
    /// indistinguishable from every other reason the guard might be idle — and it is also what a device
    /// moved to a different USB port looks like, permanently.
    /// </summary>
    private void AnnounceTargetMissing(AppSettings settings)
    {
        lock (_gate)
        {
            if (_targetMissingLogged)
                return;

            _targetMissingLogged = true;
        }

        Activity?.Invoke($"Waiting for \"{TargetName(settings)}\" — it is not available right now.");
    }

    private void RecordCorrection(AppSettings settings, string? takenBy)
    {
        bool announce;
        bool warnThrash = false;

        lock (_gate)
        {
            var now = DateTime.UtcNow;

            _corrections.Enqueue(now);
            Prune(now);

            announce = now - _lastAnnounce >= AnnounceQuietPeriod;
            if (announce)
                _lastAnnounce = now;

            if (_corrections.Count >= ThrashThreshold && !_thrashWarned)
            {
                _thrashWarned = true;
                warnThrash = true;
            }
        }

        var deviceName = TargetName(settings);

        if (announce)
        {
            // Naming the culprit is the difference between "something moved it" and a log that explains
            // itself after a boot. Only resolved on the throttled path.
            var culprit = takenBy is null ? null : _devices.GetRenderDeviceName(takenBy);

            Activity?.Invoke(culprit is null
                ? $"Default playback device was changed — set back to \"{deviceName}\"."
                : $"Default playback device was changed to \"{culprit}\" — set back to \"{deviceName}\".");
        }

        if (warnThrash)
        {
            // Something is changing it straight back. Keep holding, but stop racing it every second.
            Activity?.Invoke(
                $"Another app keeps taking the default playback device back; still holding \"{deviceName}\", " +
                "checking less often.");
        }
    }

    /// <summary>
    /// Recomputes the poll interval from what is actually happening, and re-arms only when it changed.
    /// The slowdown recovers on quiet rather than after a fixed time — the honest signal that the fight
    /// is over — which is why <see cref="Prune"/> has to run here and not only after a correction.
    /// </summary>
    private void ApplyCadence()
    {
        TimeSpan desired;

        lock (_gate)
        {
            if (_disposed || !_started)
                return;

            var now = DateTime.UtcNow;
            Prune(now);

            if (_corrections.Count < ThrashThreshold)
                _thrashWarned = false;

            desired = DesiredInterval(now);
            _recheckSoon = false;

            if (desired == _currentInterval)
                return;

            _currentInterval = desired;
        }

        Schedule(desired, dueNow: false);
    }

    /// <summary>Called under <see cref="_gate"/>, after <see cref="Prune"/>.</summary>
    private TimeSpan DesiredInterval(DateTime now)
    {
        if (_recheckSoon || now < _burstUntil)
            return BurstInterval;

        return _corrections.Count >= ThrashThreshold ? SlowInterval : Interval;
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private void Prune(DateTime now)
    {
        while (_corrections.Count > 0 && now - _corrections.Peek() > ThrashWindow)
            _corrections.Dequeue();
    }

    private static bool Enabled(AppSettings settings) =>
        settings.KeepWindowsDefault && !string.IsNullOrEmpty(settings.KeepWindowsDefaultDeviceId);

    private static string TargetName(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.KeepWindowsDefaultDeviceName)
            ? settings.KeepWindowsDefaultDeviceId
            : AudioDevice.CleanName(settings.KeepWindowsDefaultDeviceName);

    /// <summary>
    /// The endpoint to actually write. Normally the saved id; once that id has been seen to die and the
    /// saved name has been found on a live endpoint, the one it moved to.
    ///
    /// The migration is remembered against the id it came from, so a later edit in the UI — which
    /// replaces the saved id outright — drops it rather than quietly overriding the user's new choice.
    /// Nothing is persisted from here: the view model owns settings.json, and this has to keep working
    /// under --minimized, where the view model never runs.
    /// </summary>
    private string CurrentTarget(AppSettings settings)
    {
        lock (_gate)
        {
            return _resolvedTo.Length > 0 &&
                   string.Equals(_resolvedFrom, settings.KeepWindowsDefaultDeviceId, StringComparison.OrdinalIgnoreCase)
                ? _resolvedTo
                : settings.KeepWindowsDefaultDeviceId;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _watcher.Started -= OnProcessesStarted;
        _timer.Dispose();
    }
}
