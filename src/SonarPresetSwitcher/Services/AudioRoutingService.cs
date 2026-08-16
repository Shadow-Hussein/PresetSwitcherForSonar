using System.Diagnostics;
using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.Services;

/// <summary>The effective output device for a mapping, and whether a change was pushed to Windows.</summary>
/// <param name="DeviceName">Friendly name of the device in force, or null for the system default.</param>
public sealed record RoutingResult(bool Changed, string? DeviceName);

/// <summary>
/// Turns a mapping into a Windows per-app audio override.
///
/// Kept out of <see cref="PresetCoordinator"/> so that class stays purely about Sonar. The only
/// coupling is that the coordinator already knows which mapped games are running, and this needs the
/// same answer.
///
/// Two behaviours worth knowing about:
///
///   - The override Windows stores is *persistent and keyed by executable*, exactly like the one the
///     Volume mixer writes: it outlives the process and applies to the next launch. So a change is
///     only pushed when the resolved device differs from what was last set for that executable.
///   - Windows rejects the call with E_INVALIDARG until the target process has an audio session, and
///     a game is detected the moment its process appears — seconds before it initialises audio. The
///     first attempt therefore fails routinely, and <see cref="ScheduleRetry"/> keeps trying with
///     backoff until it takes or the game exits.
/// </summary>
public sealed class AudioRoutingService : IDisposable
{
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

    /// <summary>Process key → device id last pushed successfully, so a re-apply is a no-op.</summary>
    private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Process key → in-flight retry loop.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _retries = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Applies the mapping's device to every running instance of its executable.</summary>
    public RoutingResult Apply(GameMapping mapping, AppSettings settings)
    {
        var (deviceId, deviceName) = Resolve(mapping, settings);
        var effectiveName = EffectiveName(deviceId, deviceName);

        if (!IsSupported || _disposed)
            return new RoutingResult(false, effectiveName);

        var key = GameMapping.NormalizeProcessName(mapping.ProcessName);

        lock (_gate)
        {
            if (_applied.TryGetValue(key, out var last) && string.Equals(last, deviceId, StringComparison.OrdinalIgnoreCase))
                return new RoutingResult(false, effectiveName);
        }

        if (TryApply(key, deviceId))
        {
            Announce(mapping.DisplayName, effectiveName);
            return new RoutingResult(true, effectiveName);
        }

        // Almost always "the game has not opened audio yet" rather than a real failure.
        ScheduleRetry(mapping, key, deviceId, effectiveName);
        return new RoutingResult(false, effectiveName);
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
            _applied.Remove(key);

            if (_retries.Remove(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    private bool TryApply(string processKey, string deviceId)
    {
        var pids = GetProcessIds(processKey);
        if (pids.Count == 0)
            return false;

        var applied = false;
        foreach (var pid in pids)
            applied |= _devices.SetProcessOutput(pid, deviceId);

        if (!applied)
            return false;

        lock (_gate)
            _applied[processKey] = deviceId;

        return true;
    }

    private void ScheduleRetry(GameMapping mapping, string key, string deviceId, string? effectiveName)
    {
        CancellationTokenSource cts;

        lock (_gate)
        {
            if (_retries.Remove(key, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            _retries[key] = cts;
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

                    if (TryApply(key, deviceId))
                    {
                        Announce(mapping.DisplayName, effectiveName);
                        return;
                    }
                }

                Activity?.Invoke(
                    $"Gave up setting the output device for {mapping.DisplayName} — Windows kept refusing the request.");
            }
            catch (OperationCanceledException)
            {
                // The game exited, or the setting changed again.
            }
            finally
            {
                lock (_gate)
                {
                    if (_retries.TryGetValue(key, out var current) && current == cts)
                        _retries.Remove(key);
                }

                cts.Dispose();
            }
        });
    }

    private void Announce(string displayName, string? deviceName) =>
        Activity?.Invoke(deviceName is null
            ? $"Cleared the output device override for {displayName}."
            : $"Routed {displayName} to \"{deviceName}\".");

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
