using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SonarPresetSwitcher.Services.Interop;

/// <summary>
/// Access to <c>CPolicyConfigClient</c> — the undocumented COM class behind "Set as Default Device"
/// in the Sound control panel. Unlike <see cref="AudioPolicyConfig"/>, which only writes a per-app
/// override, this one moves the *system* default endpoint, and is the only way to undo an app such as
/// SteelSeries GG re-asserting its own virtual device as the default at startup.
///
/// Layout notes, in the same spirit as <see cref="AudioPolicyConfig"/>:
///
///   CLSID     870af99c-171d-4f9e-af0d-e63df40c2bc9
///   IIDs      f8679f50-850a-41cf-9c72-430f290290c8 (Windows 7 and later, including 11)
///             568b9108-44bf-40b4-9006-86afe5b5a620 (Vista, one method shorter)
///
/// The two variants differ by a single slot — Vista has no <c>ResetDeviceFormat</c> — so which one is
/// in use is settled by QueryInterface, never by a build-number guess: a wrong choice would shift
/// <c>SetDefaultEndpoint</c> onto <c>SetShareMode</c>.
///
/// The Reserved entries exist only to place <c>SetDefaultEndpoint</c> at the right offset and are
/// never called. Nothing here probes an unknown slot; the layout is confirmed after the fact by
/// <see cref="DefaultDeviceGuard"/>, which reads the default endpoint back after every write and stops
/// using the API only when it claims success and the default nevertheless does not move. A value that
/// moves and is moved back is a fight with another app, not a wrong vtable slot, which is why this
/// reports each role's HRESULT separately instead of collapsing the pair into one bool.
/// </summary>
internal sealed class PolicyConfig
{
    private static readonly Guid PolicyConfigClientClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    private readonly IPolicyConfig? _current;
    private readonly IPolicyConfigVista? _vista;

    private PolicyConfig(IPolicyConfig? current, IPolicyConfigVista? vista)
    {
        _current = current;
        _vista = vista;
    }

    public bool IsSupported => _current is not null || _vista is not null;

    /// <summary>A client that can do nothing, for callers that need an instance rather than a null.</summary>
    public static PolicyConfig Unsupported { get; } = new(null, null);

    /// <summary>
    /// Creates the client, or an unsupported instance when the class is missing or exposes neither
    /// known interface.
    /// </summary>
    public static PolicyConfig Create()
    {
        object? instance = null;

        try
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid);
            if (type is null)
                return new PolicyConfig(null, null);

            instance = Activator.CreateInstance(type);

            if (instance is IPolicyConfig current)
                return new PolicyConfig(current, null);

            if (instance is IPolicyConfigVista vista)
                return new PolicyConfig(null, vista);

            Debug.WriteLine("PolicyConfig: CPolicyConfigClient exposes neither known interface.");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or
                                       MissingMethodException or TargetInvocationException)
        {
            Debug.WriteLine($"PolicyConfig unavailable — {ex.Message}");
        }

        if (instance is not null && Marshal.IsComObject(instance))
            Marshal.ReleaseComObject(instance);

        return new PolicyConfig(null, null);
    }

    /// <summary>
    /// Makes <paramref name="mmDeviceId"/> the default endpoint for the Console and Multimedia roles —
    /// the pair the Sound control panel writes for "Set as Default Device". The Communications role is
    /// deliberately left alone, so Sonar keeps its Chat device for calls.
    /// </summary>
    public PolicyConfigResult SetDefaultEndpoint(string mmDeviceId)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(mmDeviceId))
            return PolicyConfigResult.Unsupported;

        try
        {
            // Not short-circuiting: Multimedia is written even when Console fails, because a role that
            // lands is proof the API works and half a move is better than none.
            var console = Set(mmDeviceId, ERole.Console);
            var multimedia = Set(mmDeviceId, ERole.Multimedia);

            return new PolicyConfigResult(console, multimedia);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MarshalDirectiveException)
        {
            Debug.WriteLine($"SetDefaultEndpoint failed: {ex.Message}");

            // A thrown call is Windows refusing, not Windows staying silent — carry the HRESULT so the
            // caller classifies it as a refusal rather than as a write that did nothing.
            return new PolicyConfigResult(ex.HResult, ex.HResult);
        }
    }

    private int Set(string mmDeviceId, ERole role)
    {
        var hr = _current is not null
            ? _current.SetDefaultEndpoint(mmDeviceId, role)
            : _vista!.SetDefaultEndpoint(mmDeviceId, role);

        if (hr != 0)
            Debug.WriteLine($"SetDefaultEndpoint({role}) returned 0x{hr:X8}.");

        return hr;
    }
}

/// <summary>
/// What each role's write returned. The pair is kept apart because a partial success — Console lands,
/// Multimedia comes back with a transient failure while the audio service is still starting — says the
/// API is real and working, and collapsing it into "failed" is what made the guard give up at logon.
/// </summary>
internal readonly record struct PolicyConfigResult(int ConsoleHr, int MultimediaHr)
{
    /// <summary>Both roles moved.</summary>
    public bool AllOk => ConsoleHr == 0 && MultimediaHr == 0;

    /// <summary>At least one role moved, so the call reached something real.</summary>
    public bool AnyOk => ConsoleHr == 0 || MultimediaHr == 0;

    /// <summary>No client at all, as opposed to a client that said no.</summary>
    public static PolicyConfigResult Unsupported { get; } = new(EnoInterface, EnoInterface);

    private const int EnoInterface = unchecked((int)0x80004002);
}

/// <summary>Windows 7 and later. Only <c>SetDefaultEndpoint</c> is ever called.</summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int Reserved_GetMixFormat();
    [PreserveSig] int Reserved_GetDeviceFormat();
    [PreserveSig] int Reserved_ResetDeviceFormat();
    [PreserveSig] int Reserved_SetDeviceFormat();
    [PreserveSig] int Reserved_GetProcessingPeriod();
    [PreserveSig] int Reserved_SetProcessingPeriod();
    [PreserveSig] int Reserved_GetShareMode();
    [PreserveSig] int Reserved_SetShareMode();
    [PreserveSig] int Reserved_GetPropertyValue();
    [PreserveSig] int Reserved_SetPropertyValue();

    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig] int Reserved_SetEndpointVisibility();
}

/// <summary>
/// Windows Vista, which has no <c>ResetDeviceFormat</c> slot. Unverified — no such machine was
/// available — but harmless: QueryInterface gates it, and this IID is not exposed on builds that use
/// <see cref="IPolicyConfig"/>.
/// </summary>
[ComImport]
[Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    [PreserveSig] int Reserved_GetMixFormat();
    [PreserveSig] int Reserved_GetDeviceFormat();
    [PreserveSig] int Reserved_SetDeviceFormat();
    [PreserveSig] int Reserved_GetProcessingPeriod();
    [PreserveSig] int Reserved_SetProcessingPeriod();
    [PreserveSig] int Reserved_GetShareMode();
    [PreserveSig] int Reserved_SetShareMode();
    [PreserveSig] int Reserved_GetPropertyValue();
    [PreserveSig] int Reserved_SetPropertyValue();

    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig] int Reserved_SetEndpointVisibility();
}
