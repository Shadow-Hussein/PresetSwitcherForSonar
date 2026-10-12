using System.Diagnostics;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// Polls the render endpoints Windows reports and raises an event when the set changes, so the device
/// pickers follow a headset being plugged in or pulled out without the user pressing Refresh.
///
/// Polling rather than an <c>IMMNotificationClient</c> registration, for the reason recorded on
/// <see cref="DefaultDeviceGuard"/>: every other COM call in this app creates and releases an enumerator
/// inside a single call, and a long-lived notification callback would drag apartment and lifetime
/// concerns into a service that otherwise has none. Being a couple of seconds late costs nothing here.
///
/// The one rule that is not obvious: <see cref="AudioDeviceService.GetRenderDevices"/> returns an empty
/// list both when every endpoint really is gone and when the COM call failed, and publishing the latter
/// would blank all four pickers for a tick. An empty read is therefore only believed when the read
/// before it was empty too; any non-empty read resets that.
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    private const int PollSeconds = 3;

    private readonly AudioDeviceService _devices;
    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<AudioDevice> _previous = [];
    private int _emptyReads;
    private Task? _loop;

    public AudioDeviceWatcher(AudioDeviceService devices)
    {
        _devices = devices;
    }

    /// <summary>Raised with the new device list, only when it differs from the last one reported.</summary>
    public event Action<IReadOnlyList<AudioDevice>>? DevicesChanged;

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
                Tick();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"AudioDeviceWatcher tick failed: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Tick()
    {
        var current = _devices.GetRenderDevices();

        if (current.Count == 0)
        {
            // Could be a failed enumeration rather than a machine with nothing to play through; wait for
            // a second opinion before wiping the pickers.
            if (++_emptyReads < 2)
                return;
        }
        else
        {
            _emptyReads = 0;
        }

        // AudioDevice is a record, so this compares Id and Name by value: a renamed endpoint counts as a
        // change, which is exactly what the pickers need to show.
        if (_previous.SequenceEqual(current))
            return;

        _previous = current;
        DevicesChanged?.Invoke(current);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
