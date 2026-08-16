using System.Runtime.InteropServices;

namespace SonarPresetSwitcher.Services.Interop;

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);

    /// <summary>
    /// The PROPVARIANT is passed as a raw buffer rather than a marshalled struct: it is 16 bytes on
    /// x86 and 24 on x64, and this project builds AnyCPU for debug but publishes win-x86.
    /// </summary>
    [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);

    [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
    [PreserveSig] int Commit();
}

internal static class MMDeviceApi
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public const uint DeviceStateActive = 0x00000001;
    public const uint StgmRead = 0x00000000;

    private const ushort VT_LPWSTR = 31;

    /// <summary>PKEY_Device_FriendlyName — the label Windows shows in the Sound control panel.</summary>
    private static PropertyKey FriendlyNameKey => new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 14,
    };

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)
                   ?? throw new NotSupportedException("MMDeviceEnumerator is not registered.");

        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    /// <summary>Reads PKEY_Device_FriendlyName, or null when the property is missing.</summary>
    public static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0 || store is null)
            return null;

        // 24 covers sizeof(PROPVARIANT) on both x86 and x64; over-allocating is harmless.
        var buffer = Marshal.AllocCoTaskMem(24);
        try
        {
            for (var i = 0; i < 24; i++)
                Marshal.WriteByte(buffer, i, 0);

            var key = FriendlyNameKey;
            if (store.GetValue(ref key, buffer) != 0)
                return null;

            try
            {
                if ((ushort)Marshal.ReadInt16(buffer) != VT_LPWSTR)
                    return null;

                // The union starts at offset 8 on both architectures (2-byte vt + 6 reserved bytes).
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, 8));
            }
            finally
            {
                PropVariantClear(buffer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
            Marshal.ReleaseComObject(store);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr pvar);
}
