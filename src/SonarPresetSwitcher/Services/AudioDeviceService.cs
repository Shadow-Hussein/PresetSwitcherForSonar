using System.Diagnostics;
using System.Runtime.InteropServices;
using SonarPresetSwitcher.Models;
using SonarPresetSwitcher.Services.Interop;

namespace SonarPresetSwitcher.Services;

/// <summary>
/// The app's whole view of Windows audio: enumerate output endpoints, point an individual process at
/// one of them, and read or move the system default endpoint. Nothing above this class touches COM.
///
/// Every entry point swallows interop failures and degrades to "unsupported" rather than throwing —
/// both policy APIs are undocumented, so a future Windows build changing them must cost the user a
/// greyed-out dropdown, not a crash on startup.
/// </summary>
public sealed class AudioDeviceService
{
    /// <summary>
    /// Grace for a logon where the audio service is not up yet: creating <c>CPolicyConfigClient</c> can
    /// fail for the first few seconds after boot, and caching that answer for the process lifetime is
    /// indistinguishable from a build of Windows that cannot do this at all. After the last entry,
    /// unsupported is final.
    /// </summary>
    private static readonly TimeSpan[] DefaultsRetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
    ];

    private readonly Lazy<AudioPolicyConfig> _policy =
        new(AudioPolicyConfig.Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lock _defaultsGate = new();

    /// <summary>Last client created. Kept even when unsupported, so the retry budget can be spent.</summary>
    private PolicyConfig? _defaults;

    private int _defaultsAttempts;
    private DateTime _defaultsRetryAfter = DateTime.MinValue;

    /// <summary>False when per-app routing is unavailable; the UI disables its pickers.</summary>
    public bool IsSupported
    {
        get
        {
            try
            {
                return _policy.Value.IsSupported;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AudioDeviceService.IsSupported failed: {ex}");
                return false;
            }
        }
    }

    /// <summary>
    /// Last known answer to "can the system default endpoint be moved on this machine". Deliberately
    /// does not create anything: <see cref="EnsureDefaultDeviceSupport"/> is what establishes it, so
    /// that COM activation happens on the guard's timer thread rather than on whichever thread happens
    /// to read this property. Safe to call from the UI thread and on every poll.
    /// </summary>
    public bool CanSetDefaultDevice
    {
        get
        {
            lock (_defaultsGate)
                return _defaults?.IsSupported ?? false;
        }
    }

    /// <summary>
    /// Creates the default-endpoint client if there is none, or retries a failed creation once its
    /// cooldown has passed and the attempt budget still allows. A working client is kept for the
    /// process lifetime. Returns whether the system default can be moved right now.
    ///
    /// Called from <see cref="DefaultDeviceGuard"/>'s timer thread, which is also what keeps
    /// <c>CPolicyConfigClient</c> out of the WPF STA — activating it there would proxy every later
    /// write through the UI thread's message queue, and a busy logon is exactly when that fails.
    /// </summary>
    public bool EnsureDefaultDeviceSupport()
    {
        lock (_defaultsGate)
        {
            if (_defaults is { IsSupported: true })
                return true;

            if (_defaults is not null &&
                (_defaultsAttempts >= DefaultsRetryDelays.Length || DateTime.UtcNow < _defaultsRetryAfter))
            {
                return false;
            }

            var created = Create();
            if (created.IsSupported)
            {
                _defaults = created;
                return true;
            }

            // No client, or one that exposes neither interface. Remember it so the property has an
            // answer, and let the next call try again once the cooldown is up.
            _defaults = created;
            _defaultsRetryAfter = DateTime.UtcNow + DefaultsRetryDelays[
                Math.Min(_defaultsAttempts, DefaultsRetryDelays.Length - 1)];
            _defaultsAttempts++;

            return false;
        }
    }

    /// <summary><see cref="PolicyConfig.Create"/> already swallows its own failures; this is the belt.</summary>
    private static PolicyConfig Create()
    {
        try
        {
            return PolicyConfig.Create();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioDeviceService.EnsureDefaultDeviceSupport failed: {ex}");
            return PolicyConfig.Unsupported;
        }
    }

    /// <summary>True once the retry budget for creating the default-endpoint client is spent.</summary>
    public bool DefaultDeviceSupportSettled
    {
        get
        {
            lock (_defaultsGate)
                return _defaults is { IsSupported: true } || _defaultsAttempts >= DefaultsRetryDelays.Length;
        }
    }

    /// <summary>Active render endpoints, alphabetically. Empty when enumeration fails.</summary>
    public IReadOnlyList<AudioDevice> GetRenderDevices()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            enumerator = MMDeviceApi.CreateEnumerator();

            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, MMDeviceApi.DeviceStateActive, out collection) != 0 ||
                collection is null)
            {
                return [];
            }

            if (collection.GetCount(out var count) != 0)
                return [];

            var devices = new List<AudioDevice>((int)count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) != 0 || device is null)
                        continue;

                    if (device.GetId(out var id) != 0 || string.IsNullOrEmpty(id))
                        continue;

                    devices.Add(new AudioDevice(id, MMDeviceApi.GetFriendlyName(device) ?? id));
                }
                finally
                {
                    if (device is not null)
                        Marshal.ReleaseComObject(device);
                }
            }

            return devices
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            Debug.WriteLine($"GetRenderDevices failed: {ex}");
            return [];
        }
        finally
        {
            if (collection is not null)
                Marshal.ReleaseComObject(collection);
            if (enumerator is not null)
                Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Points one process at a render endpoint. Null or empty clears the override, handing the
    /// process back to the Windows default device.
    /// </summary>
    public bool SetProcessOutput(int processId, string? mmDeviceId)
    {
        try
        {
            return _policy.Value.SetProcessOutput(processId, mmDeviceId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetProcessOutput({processId}) failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Id of the current default playback device for the Console role, or null when there is none —
    /// which is also what a machine with every endpoint disabled reports.
    /// </summary>
    public string? GetDefaultRenderDeviceId()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            enumerator = MMDeviceApi.CreateEnumerator();

            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device) != 0 ||
                device is null)
            {
                return null;
            }

            return device.GetId(out var id) == 0 && !string.IsNullOrEmpty(id) ? id : null;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            Debug.WriteLine($"GetDefaultRenderDeviceId failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (device is not null)
                Marshal.ReleaseComObject(device);
            if (enumerator is not null)
                Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// How much of the system default the given endpoint currently holds. Both roles are read because
    /// <see cref="SetDefaultRenderDevice"/> writes both: a steady state of Console on one device and
    /// Multimedia on another is otherwise invisible, and it is exactly what a repeated per-role failure
    /// leaves behind.
    ///
    /// Returning a hold rather than a role keeps <c>ERole</c> inside Interop, where it belongs.
    /// </summary>
    public DefaultDeviceHold GetDefaultRenderHold(string mmDeviceId)
    {
        if (string.IsNullOrWhiteSpace(mmDeviceId))
            return DefaultDeviceHold.None;

        IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = MMDeviceApi.CreateEnumerator();

            var console = HoldsRole(enumerator, ERole.Console, mmDeviceId);
            var multimedia = HoldsRole(enumerator, ERole.Multimedia, mmDeviceId);

            return (console, multimedia) switch
            {
                (true, true) => DefaultDeviceHold.Both,
                (false, false) => DefaultDeviceHold.None,
                _ => DefaultDeviceHold.Partial,
            };
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            Debug.WriteLine($"GetDefaultRenderHold({mmDeviceId}) failed: {ex.Message}");
            return DefaultDeviceHold.None;
        }
        finally
        {
            if (enumerator is not null)
                Marshal.ReleaseComObject(enumerator);
        }
    }

    private static bool HoldsRole(IMMDeviceEnumerator enumerator, ERole role, string mmDeviceId)
    {
        IMMDevice? device = null;

        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out device) != 0 || device is null)
                return false;

            return device.GetId(out var id) == 0 &&
                   string.Equals(id, mmDeviceId, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (device is not null)
                Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Friendly name of one endpoint, or null when it cannot be read. For log lines.</summary>
    public string? GetRenderDeviceName(string mmDeviceId)
    {
        if (string.IsNullOrWhiteSpace(mmDeviceId))
            return null;

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            enumerator = MMDeviceApi.CreateEnumerator();

            if (enumerator.GetDevice(mmDeviceId, out device) != 0 || device is null)
                return null;

            return MMDeviceApi.GetFriendlyName(device);
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            Debug.WriteLine($"GetRenderDeviceName({mmDeviceId}) failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (device is not null)
                Marshal.ReleaseComObject(device);
            if (enumerator is not null)
                Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>True when the endpoint exists and is plugged in and enabled.</summary>
    public bool IsRenderDeviceActive(string mmDeviceId)
    {
        if (string.IsNullOrWhiteSpace(mmDeviceId))
            return false;

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            enumerator = MMDeviceApi.CreateEnumerator();

            if (enumerator.GetDevice(mmDeviceId, out device) != 0 || device is null)
                return false;

            return device.GetState(out var state) == 0 && state == MMDeviceApi.DeviceStateActive;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
        {
            Debug.WriteLine($"IsRenderDeviceActive({mmDeviceId}) failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (device is not null)
                Marshal.ReleaseComObject(device);
            if (enumerator is not null)
                Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Moves the *system* default playback device, exactly as the Sound control panel does. Only
    /// <see cref="DefaultDeviceGuard"/> calls this, and only when the user has asked for it.
    /// </summary>
    public DefaultDeviceWriteResult SetDefaultRenderDevice(string mmDeviceId)
    {
        PolicyConfig? client;

        lock (_defaultsGate)
            client = _defaults;

        if (client is not { IsSupported: true })
            return DefaultDeviceWriteResult.Unsupported;

        try
        {
            var result = client.SetDefaultEndpoint(mmDeviceId);

            if (result.AllOk)
                return DefaultDeviceWriteResult.Accepted;

            return result.AnyOk ? DefaultDeviceWriteResult.PartlyAccepted : DefaultDeviceWriteResult.Refused;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetDefaultRenderDevice({mmDeviceId}) failed: {ex}");
            return DefaultDeviceWriteResult.Refused;
        }
    }
}

/// <summary>How much of the system default one endpoint holds — see the Console/Multimedia split.</summary>
public enum DefaultDeviceHold
{
    None,
    Partial,
    Both,
}

/// <summary>
/// What Windows made of a request to move the system default playback device. The distinction that
/// matters is <see cref="Refused"/> (Windows said no, usually transient) versus <see cref="Accepted"/>
/// with nothing having moved, which is the only signature of a wrong vtable slot.
/// </summary>
public enum DefaultDeviceWriteResult
{
    /// <summary>No usable client — nothing was attempted.</summary>
    Unsupported,

    /// <summary>Every role returned success.</summary>
    Accepted,

    /// <summary>One role landed and the other did not, so the API itself is real.</summary>
    PartlyAccepted,

    /// <summary>Windows returned a failure for every role.</summary>
    Refused,
}
