using System.Diagnostics;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// Polls the process table and reports which executables appeared or disappeared.
///
/// Polling is used rather than a WMI __InstanceCreationEvent subscription because it needs no WMI
/// permissions, has no COM lifetime pitfalls, and its first tick naturally picks up games that were
/// already running when the app started. Reading Process.ProcessName does not require elevation, so
/// anti-cheat titles running elevated are still detected.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private HashSet<string> _previous = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;
    private volatile int _intervalSeconds = 2;

    /// <summary>Raised with the set of process names (no extension, lower-cased) that just started.</summary>
    public event Action<IReadOnlyCollection<string>>? Started;

    /// <summary>Raised with the set of process names that just exited.</summary>
    public event Action<IReadOnlyCollection<string>>? Stopped;

    /// <summary>Snapshot of everything currently running, refreshed each tick.</summary>
    public IReadOnlySet<string> RunningProcesses => _previous;

    public bool IsPaused { get; set; }

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => _intervalSeconds = Math.Clamp(value, 1, 30);
    }

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsPaused)
                    Tick();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"ProcessWatcher tick failed: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Tick()
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                current.Add(process.ProcessName);
            }
            catch (InvalidOperationException)
            {
                // The process exited between enumeration and the property read.
            }
            finally
            {
                process.Dispose();
            }
        }

        var started = current.Except(_previous, StringComparer.OrdinalIgnoreCase).ToList();
        var stopped = _previous.Except(current, StringComparer.OrdinalIgnoreCase).ToList();
        _previous = current;

        if (started.Count > 0)
            Started?.Invoke(started);

        if (stopped.Count > 0)
            Stopped?.Invoke(stopped);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
