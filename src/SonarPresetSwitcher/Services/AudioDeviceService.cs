using System.Diagnostics;
using System.Runtime.InteropServices;
using SonarPresetSwitcher.Models;
using SonarPresetSwitcher.Services.Interop;

namespace SonarPresetSwitcher.Services;

/// <summary>
/// The app's whole view of Windows audio: enumerate output endpoints, and point an individual
/// process at one of them. Nothing above this class touches COM.
///
/// Every entry point swallows interop failures and degrades to "unsupported" rather than throwing —
/// the per-app routing API is undocumented, so a future Windows build changing it must cost the user
/// a greyed-out dropdown, not a crash on startup.
/// </summary>
public sealed class AudioDeviceService
{
    private readonly Lazy<AudioPolicyConfig> _policy =
        new(AudioPolicyConfig.Create, LazyThreadSafetyMode.ExecutionAndPublication);

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
}
