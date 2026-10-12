using System.Diagnostics;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>The effective output device for a mapping, and whether a change was pushed to Windows.</summary>
/// <param name="DeviceName">Friendly name of the device in force, or null for the system default.</param>
public sealed record RoutingResult(bool Changed, string? DeviceName);

/// <summary>
/// Turns a mapping or a recording app into a Windows per-app audio override.
///
/// Kept out of <see cref="PresetCoordinator"/> so that class stays purely about Sonar. The only
/// coupling is that the coordinator already knows which mapped processes are running, and this needs
/// the same answer.
///
/// The two callers are independent features — <see cref="Apply"/> sends a game to an output device,
/// <see cref="ApplyInput"/> points a recording app at a microphone — but they land on the same pair of
/// Windows overrides for one executable, which is why both go through this class. Windows stores and
/// accepts the two directions separately, so each is tracked, pushed and retried on its own.
///
/// Two behaviours worth knowing about:
///
///   - The override Windows stores is *persistent and keyed by executable*, exactly like the one the
///     Volume mixer writes: it outlives the process and applies to the next launch. So a change is
///     only pushed when the resolved device differs from what was last set for that executable.
///   - Windows rejects the call with E_INVALIDARG until the target process has an audio session, and
///     a game is detected the moment its process appears — seconds before it initialises audio. The
///     first attempt therefore fails routinely, and <see cref="ScheduleRetry"/> keeps trying with
///     backoff until it takes or the game exits. A recording app opens its capture session on its own
///     schedule, which is exactly why the two directions cannot share one retry loop.
/// </summary>
public sealed class AudioRoutingService : IDisposable
{
    /// <summary>
    /// Which half of a mapping's audio a push is for. Windows keeps the two overrides separately and
    /// accepts them independently, so everything below is tracked per direction rather than per process.
    /// </summary>
    private enum Direction
    {
        Output,
        Input,
    }

    private static readonly Direction[] Directions = [Direction.Output, Direction.Input];

    /// <summary>Backoff for the retry loop. Long enough in total to cover a slow-loading game.</summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
    ];

    private readonly AudioDeviceService _devices;
    private readonly Lock _gate = new();

    /// <summary>
    /// (process key, direction) → device id last pushed successfully, so a re-apply is a no-op.
    ///
    /// Keyed on the pair rather than the process because the two directions succeed independently: an
    /// app can have a render session and no capture session, or the other way round, so each needs its
    /// own memory and its own retry. Process keys all come from
    /// <see cref="GameMapping.NormalizeProcessName"/>, which lower-cases, so the default comparer is
    /// already case-insensitive in practice.
    /// </summary>
    private readonly Dictionary<(string Process, Direction Direction), string> _applied = [];

    /// <summary>(process key, direction) → in-flight retry loop.</summary>
    private readonly Dictionary<(string Process, Direction Direction), CancellationTokenSource> _retries = [];

    private bool _disposed;

    public AudioRoutingService(AudioDeviceService devices)
    {
        _devices = devices;
    }

    /// <summary>Fired with a short human-readable line for the activity log.</summary>
    public event Action<string>? Activity;

    public bool IsSupported => _devices.IsSupported;

    /// <summary>
    /// Resolves the device for a mapping — the mapping's own choice, or the global default when it is
    /// set to inherit.
    /// </summary>
    public static (string Id, string Name) Resolve(GameMapping mapping, AppSettings settings) =>
        string.Equals(mapping.OutputDeviceId, AudioDevice.InheritId, StringComparison.Ordinal)
            ? (settings.DefaultOutputDeviceId, settings.DefaultOutputDeviceName)
            : (mapping.OutputDeviceId, mapping.OutputDeviceName);

    /// <summary>Sends a mapped game's audio to its output device.</summary>
    /// <returns>The effective output device, which is what the coordinator reports to the UI.</returns>
    public RoutingResult Apply(GameMapping mapping, AppSettings settings)
    {
        var (deviceId, deviceName) = Resolve(mapping, settings);
        var effectiveName = EffectiveName(deviceId, deviceName);

        if (!IsSupported || _disposed)
            return new RoutingResult(false, effectiveName);

        var key = GameMapping.NormalizeProcessName(mapping.ProcessName);
        var changed = ApplyDirection(
            mapping.DisplayName, key, Direction.Output, deviceId, effectiveName);

        return new RoutingResult(changed, effectiveName);
    }

    /// <summary>
    /// Points a recording app at a microphone. The capture counterpart of <see cref="Apply"/>, kept
    /// separate because the two features are: a game names an output, a recording app names an input,
    /// and neither list is obliged to mention the other half.
    ///
    /// <see cref="AudioDevice.UnmanagedId"/> is the absence of an instruction, so it is skipped
    /// entirely rather than pushed as a clear — nothing about the app's input is touched.
    /// </summary>
    public void ApplyInput(string processName, string displayName, string deviceId, string deviceName)
    {
        if (!IsSupported || _disposed ||
            string.Equals(deviceId, AudioDevice.UnmanagedId, StringComparison.Ordinal))
            return;

        ApplyDirection(
            displayName,
            GameMapping.NormalizeProcessName(processName),
            Direction.Input,
            deviceId,
            EffectiveName(deviceId, deviceName));
    }

    /// <summary>One direction for one executable. True when a change was pushed to Windows just now.</summary>
    private bool ApplyDirection(
        string displayName, string key, Direction direction, string deviceId, string? effectiveName)
    {
        lock (_gate)
        {
            if (_applied.TryGetValue((key, direction), out var last) &&
                string.Equals(last, deviceId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (TryApply(key, direction, deviceId))
        {
            Announce(displayName, direction, effectiveName);
            return true;
        }

        // Almost always "the app has not opened audio yet" rather than a real failure.
        ScheduleRetry(displayName, key, direction, deviceId, effectiveName);
        return false;
    }

    /// <summary>
    /// Drops the cached state for an executable that has exited, so the next launch re-applies, and
    /// stops any retry loop still waiting on it. The override itself is left in place — that is what
    /// makes it stick for the next launch, and it is how the Volume mixer behaves too.
    /// </summary>
    public void Forget(string processName)
    {
        var key = GameMapping.NormalizeProcessName(processName);

        lock (_gate)
        {
            foreach (var direction in Directions)
            {
                _applied.Remove((key, direction));

                if (_retries.Remove((key, direction), out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
        }
    }

    private bool TryApply(string processKey, Direction direction, string deviceId)
    {
        var pids = GetProcessIds(processKey);
        if (pids.Count == 0)
            return false;

        var applied = false;
        foreach (var pid in pids)
        {
            applied |= direction == Direction.Output
                ? _devices.SetProcessOutput(pid, deviceId)
                : _devices.SetProcessInput(pid, deviceId);
        }

        if (!applied)
            return false;

        lock (_gate)
            _applied[(processKey, direction)] = deviceId;

        return true;
    }

    private void ScheduleRetry(
        string displayName, string key, Direction direction, string deviceId, string? effectiveName)
    {
        CancellationTokenSource cts;

        lock (_gate)
        {
            if (_retries.Remove((key, direction), out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            _retries[(key, direction)] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in RetryDelays)
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);

                    if (GetProcessIds(key).Count == 0)
                        return;

                    if (TryApply(key, direction, deviceId))
                    {
                        Announce(displayName, direction, effectiveName);
                        return;
                    }
                }

                Activity?.Invoke(
                    $"Gave up setting the {Noun(direction)} device for {displayName} — " +
                    "Windows kept refusing the request.");
            }
            catch (OperationCanceledException)
            {
                // The game exited, or the setting changed again.
            }
            finally
            {
                lock (_gate)
                {
                    if (_retries.TryGetValue((key, direction), out var current) && current == cts)
                        _retries.Remove((key, direction));
                }

                cts.Dispose();
            }
        });
    }

    private void Announce(string displayName, Direction direction, string? deviceName) =>
        Activity?.Invoke(deviceName is null
            ? $"Cleared the {Noun(direction)} device override for {displayName}."
            : direction == Direction.Output
                ? $"Routed {displayName} to \"{deviceName}\"."
                : $"Set {displayName} to record from \"{deviceName}\".");

    private static string Noun(Direction direction) =>
        direction == Direction.Output ? "output" : "recording";

    private static string? EffectiveName(string deviceId, string deviceName)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        return string.IsNullOrWhiteSpace(deviceName) ? deviceId : deviceName;
    }

    private static List<int> GetProcessIds(string processKey)
    {
        var ids = new List<int>();

        try
        {
            foreach (var process in Process.GetProcessesByName(processKey))
            {
                try
                {
                    ids.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                    // Exited between enumeration and the property read.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"GetProcessIds({processKey}) failed: {ex.Message}");
        }

        return ids;
    }

    public void Dispose()
    {
        _disposed = true;

        lock (_gate)
        {
            foreach (var cts in _retries.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _retries.Clear();
        }
    }
}
