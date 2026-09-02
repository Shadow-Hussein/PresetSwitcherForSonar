using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SonarPresetSwitcher.Services.Interop;

/// <summary>
/// Access to <c>Windows.Media.Internal.AudioPolicyConfig</c> — the undocumented interface behind
/// Settings → Sound → Volume mixer → "App output device". It sets a *persisted per-application*
/// default endpoint; the system default device is never touched.
///
/// The interface is not in the Windows SDK, so the layout below was established empirically against
/// Windows 11 build 26200 rather than taken on faith (set an endpoint on a live process, read the
/// exact string back, clear it, read empty):
///
///   IID       ab3d4648-e242-459f-b02f-541c70306324 — the only variant this build exposes.
///   vtable    IUnknown (3), IInspectable (3), 19 IAudioPolicyConfig methods we never call,
///             then SetPersistedDefaultAudioEndpoint, GetPersistedDefaultAudioEndpoint,
///             ClearAllPersistedApplicationDefaultEndpoints.
///
/// Two things that look like they should work but do not, and cost real debugging time:
///
///   - <c>ComInterfaceType.InterfaceIsIInspectable</c> throws PlatformNotSupportedException on .NET
///     (built-in WinRT interop was removed in .NET 5). Existing C# implementations of this API rely
///     on it, so their declarations omit IInspectable's three slots; ours must declare them.
///   - <c>UnmanagedType.HString</c> is gone for the same reason, so HSTRINGs are created and freed by
///     hand and passed as IntPtr. IntPtr.Zero is a valid empty HSTRING, which is how an override is
///     cleared.
///
/// Which variant the OS exposes is settled by QueryInterface, never by a build-number guess: the
/// IIDs differ, so an unsupported variant fails the cast instead of calling into a wrong vtable slot.
/// </summary>
internal sealed class AudioPolicyConfig
{
    private const string ActivatableClassId = "Windows.Media.Internal.AudioPolicyConfig";

    /// <summary>DEVINTERFACE_AUDIO_RENDER — part of the device path the API expects.</summary>
    private const string RenderInterfaceGuid = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    /// <summary>
    /// DEVINTERFACE_AUDIO_CAPTURE. The device path is built from the interface matching the data flow,
    /// so a capture endpoint set with the render GUID is rejected rather than mis-routed.
    /// </summary>
    private const string CaptureInterfaceGuid = "{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private const int E_INVALIDARG = unchecked((int)0x80070057);

    private readonly IAudioPolicyConfigCurrent? _current;
    private readonly IAudioPolicyConfigLegacy? _legacy;

    private AudioPolicyConfig(IAudioPolicyConfigCurrent? current, IAudioPolicyConfigLegacy? legacy)
    {
        _current = current;
        _legacy = legacy;
    }

    public bool IsSupported => _current is not null || _legacy is not null;

    /// <summary>
    /// Activates the factory and verifies the vtable layout, or returns an unsupported instance when
    /// neither variant works on this build of Windows.
    /// </summary>
    public static AudioPolicyConfig Create()
    {
        if (TryActivate<IAudioPolicyConfigCurrent>(out var current) &&
            Probe(current!.GetPersistedDefaultAudioEndpoint))
        {
            return new AudioPolicyConfig(current, null);
        }

        if (TryActivate<IAudioPolicyConfigLegacy>(out var legacy) &&
            Probe(legacy!.GetPersistedDefaultAudioEndpoint))
        {
            return new AudioPolicyConfig(null, legacy);
        }

        Debug.WriteLine("AudioPolicyConfig: no usable variant on this build of Windows.");
        return new AudioPolicyConfig(null, null);
    }

    private static bool TryActivate<T>(out T? result) where T : class
    {
        result = null;

        var classId = IntPtr.Zero;
        try
        {
            WindowsCreateString(ActivatableClassId, (uint)ActivatableClassId.Length, out classId);

            var iid = typeof(T).GUID;
            RoGetActivationFactory(classId, ref iid, out var factory);

            result = factory as T;
            return result is not null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MarshalDirectiveException)
        {
            Debug.WriteLine($"AudioPolicyConfig: {typeof(T).Name} unavailable — {ex.Message}");
            return false;
        }
        finally
        {
            if (classId != IntPtr.Zero)
                WindowsDeleteString(classId);
        }
    }

    private delegate int GetEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

    /// <summary>
    /// Read-only smoke test of the vtable layout, against our own process id.
    ///
    /// E_INVALIDARG counts as a pass: the API answers that for any process without an audio session,
    /// and this app has none. What it rules out is landing on a slot that is not a real method.
    /// </summary>
    private static bool Probe(GetEndpoint get)
    {
        try
        {
            var hr = get((uint)Environment.ProcessId, EDataFlow.Render, ERole.Multimedia, out var value);
            if (value != IntPtr.Zero)
                WindowsDeleteString(value);

            return hr == 0 || hr == E_INVALIDARG;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioPolicyConfig probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Routes a process to a render endpoint. Pass null or empty to clear the override and hand the
    /// process back to the system default.
    /// </summary>
    /// <returns>
    /// False when Windows rejected the request — most often because the process has not opened an
    /// audio session yet, which is normal for a game in the first seconds after launch and is why
    /// <see cref="AudioRoutingService"/> retries.
    /// </returns>
    public bool SetProcessOutput(int processId, string? mmDeviceId) =>
        SetProcessEndpoint(processId, mmDeviceId, EDataFlow.Render);

    /// <summary>
    /// The same for a capture endpoint — the "App input device" half of the Volume mixer. This is how a
    /// recording or chat app is pointed at one of Sonar's virtual microphones.
    /// </summary>
    /// <returns>
    /// False on rejection, which for capture has the same benign cause as for render: the process has
    /// not opened a capture session yet. See <see cref="AudioRoutingService"/>.
    /// </returns>
    public bool SetProcessInput(int processId, string? mmDeviceId) =>
        SetProcessEndpoint(processId, mmDeviceId, EDataFlow.Capture);

    private bool SetProcessEndpoint(int processId, string? mmDeviceId, EDataFlow flow)
    {
        if (!IsSupported)
            return false;

        var interfaceGuid = flow == EDataFlow.Render ? RenderInterfaceGuid : CaptureInterfaceGuid;

        var path = string.IsNullOrWhiteSpace(mmDeviceId)
            ? ""
            : $@"\\?\SWD#MMDEVAPI#{mmDeviceId}#{interfaceGuid}";

        var handle = IntPtr.Zero;
        try
        {
            // A null HSTRING is the empty string, which is exactly how the API is told to clear.
            if (path.Length > 0)
                WindowsCreateString(path, (uint)path.Length, out handle);

            // The mixer writes both roles; matching that keeps our state indistinguishable from a
            // change the user made by hand in Settings. Communications is left alone for capture too:
            // the mixer's own input picker does not write it, and an app that asks for the
            // communications role should keep following the user's Windows-wide choice.
            return Set(processId, flow, ERole.Console, handle) & Set(processId, flow, ERole.Multimedia, handle);
        }
        catch (Exception ex) when (ex is COMException or MarshalDirectiveException or InvalidCastException)
        {
            Debug.WriteLine($"SetProcessEndpoint({processId}, {flow}) failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                WindowsDeleteString(handle);
        }
    }

    private bool Set(int processId, EDataFlow flow, ERole role, IntPtr deviceId)
    {
        var hr = _current is not null
            ? _current.SetPersistedDefaultAudioEndpoint((uint)processId, flow, role, deviceId)
            : _legacy!.SetPersistedDefaultAudioEndpoint((uint)processId, flow, role, deviceId);

        if (hr != 0)
            Debug.WriteLine($"SetPersistedDefaultAudioEndpoint(pid {processId}, {flow}, {role}) returned 0x{hr:X8}.");

        return hr == 0;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);
}

/// <summary>
/// Windows 10 21H2 and later, including Windows 11. Verified against build 26200.
///
/// The three IInspectable slots are declared explicitly because .NET cannot marshal
/// InterfaceIsIInspectable; the nineteen Reserved entries only exist to place the three methods that
/// follow them at the right vtable offsets, and are never called.
/// </summary>
[ComImport]
[Guid("AB3D4648-E242-459F-B02F-541C70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigCurrent
{
    [PreserveSig] int GetIids(out uint count, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    [PreserveSig] int Reserved_AddCtxVolumeChange();
    [PreserveSig] int Reserved_RemoveCtxVolumeChanged();
    [PreserveSig] int Reserved_AddRingerVibrateStateChanged();
    [PreserveSig] int Reserved_RemoveRingerVibrateStateChange();
    [PreserveSig] int Reserved_SetVolumeGroupGainForId();
    [PreserveSig] int Reserved_GetVolumeGroupGainForId();
    [PreserveSig] int Reserved_GetActiveVolumeGroupForEndpointId();
    [PreserveSig] int Reserved_GetVolumeGroupsForEndpoint();
    [PreserveSig] int Reserved_GetCurrentVolumeContext();
    [PreserveSig] int Reserved_SetVolumeGroupMuteForId();
    [PreserveSig] int Reserved_GetVolumeGroupMuteForId();
    [PreserveSig] int Reserved_SetRingerVibrateState();
    [PreserveSig] int Reserved_GetRingerVibrateState();
    [PreserveSig] int Reserved_SetPreferredChatApplication();
    [PreserveSig] int Reserved_ResetPreferredChatApplication();
    [PreserveSig] int Reserved_GetPreferredChatApplication();
    [PreserveSig] int Reserved_GetCurrentChatApplications();
    [PreserveSig] int Reserved_AddChatContextChanged();
    [PreserveSig] int Reserved_RemoveChatContextChanged();

    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
    [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
    [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
}

/// <summary>
/// Windows 10 before 21H2, where the three methods sit directly after IInspectable. Unverified — no
/// such machine was available — but harmless: QueryInterface gates it, and this IID is not exposed on
/// builds that use <see cref="IAudioPolicyConfigCurrent"/>.
/// </summary>
[ComImport]
[Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigLegacy
{
    [PreserveSig] int GetIids(out uint count, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);
    [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
    [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
}
