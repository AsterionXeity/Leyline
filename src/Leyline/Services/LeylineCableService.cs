using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace Leyline.Services;

/// <summary>A virtual cable of the Leyline Virtual Audio driver (LEYLINE_CABLES in driver\LeylineVAD\src\LeylineShared.h).</summary>
public sealed record LeylineCable(string Id, string DisplayName, bool IsMixOutput);

/// <summary>
/// Controls the Leyline driver's cables. Each cable is a playback endpoint looped to a recording endpoint
/// ("System (Leyline Virtual Audio)"). Cables start disconnected (shown as "Disconnected" in Windows); Leyline
/// connects the ones its channels and mixes use through the driver's private KSPROPSETID_Leyline property.
/// </summary>
public static class LeylineCableService
{
    // Keep in sync with LEYLINE_CABLES in LeylineShared.h.
    public static readonly IReadOnlyList<LeylineCable> Cables =
    [
        new("System",     "System",     IsMixOutput: false),
        new("Game",       "Game",       IsMixOutput: false),
        new("Browser",    "Browser",    IsMixOutput: false),
        new("Music",      "Music",      IsMixOutput: false),
        new("VoiceChat",  "Voice Chat", IsMixOutput: false),
        new("StreamMix",  "Stream Mix", IsMixOutput: true),    // carries the Stream Mix (OBS records it)
        new("Microphone", "Microphone", IsMixOutput: true),    // carries the Mic Rec mix (Discord uses it as a mic)
    ];

    public const string StreamMixCableId = "StreamMix";
    public const string MicrophoneCableId = "Microphone";

    private static readonly Guid KSPROPSETID_Leyline = new("FCEF5F84-A2A9-45D3-9D90-AEEE9E67A1B0");
    private const uint KSPROPERTY_LEYLINE_CABLE_CONNECTED = 1;
    private static readonly Guid KSCATEGORY_TOPOLOGY = new("DDA54A40-1E4C-11D1-A050-405705C10000");

    // PKEY_AudioEndpoint's KS filter interface: "{2}.\\?\root#media#0008#{...}\waverendersystem"
    private static readonly PropertyKey PKEY_EndpointFilterInterface = new(new Guid("233164c8-1b2c-4c7d-bc68-b671687a2567"), 1);

    public static LeylineCable? Find(string? cableId) =>
        Cables.FirstOrDefault(c => c.Id.Equals(cableId, StringComparison.OrdinalIgnoreCase));

    //=========================================================================
    // Connection
    //=========================================================================

    /// <summary>True when the Leyline driver's cable filters are present.</summary>
    public static bool IsDriverRunning() => FindTopologyFilter(Cables[0].Id) != null;

    public static bool? IsConnected(string cableId)
    {
        string? filter = FindTopologyFilter(cableId);
        return filter != null && TryGetProperty(filter, out uint value) ? value != 0 : null;
    }

    /// <summary>Connects or disconnects a cable (both its playback and recording endpoints).</summary>
    public static bool SetConnected(string cableId, bool connected)
    {
        string? filter = FindTopologyFilter(cableId);
        if (filter == null)
            return false;

        bool ok = TrySetProperty(filter, connected ? 1u : 0u);
        if (!ok)
            LeylineDriverService.Log($"Setting cable {cableId} connected={connected} failed (Win32 error {Marshal.GetLastWin32Error()})");
        return ok;
    }

    //=========================================================================
    // Endpoints
    //=========================================================================

    /// <summary>The cable's playback (Render) or recording (Capture) endpoint ID, or null when not present.</summary>
    public static string? FindEndpointId(string cableId, DataFlow flow, bool activeOnly = true)
    {
        string suffix = (flow == DataFlow.Render ? "\\waverender" : "\\wavecapture") + cableId;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var states = activeOnly ? DeviceState.Active : DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled;
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, states))
            {
                try
                {
                    var store = device.Properties;
                    if (store.Contains(PKEY_EndpointFilterInterface) &&
                        store[PKEY_EndpointFilterInterface].Value is string iface &&
                        iface.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return device.ID;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Connects the cable and waits until its endpoints are active (Windows takes a moment to bring them up).
    /// Returns the playback and recording endpoint IDs, or nulls when the driver is not running.
    /// </summary>
    public static (string? RenderId, string? CaptureId) ConnectAndResolve(string cableId, int timeoutMs = 4000)
    {
        if (IsConnected(cableId) != true && !SetConnected(cableId, true))
            return (null, null);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            string? render = FindEndpointId(cableId, DataFlow.Render);
            string? capture = FindEndpointId(cableId, DataFlow.Capture);
            if ((render != null && capture != null) || DateTime.UtcNow > deadline)
                return (render, capture);
            Thread.Sleep(100);
        }
    }

    //=========================================================================
    // KS property access (IOCTL_KS_PROPERTY on the cable's render topology filter)
    //=========================================================================

    private const uint IOCTL_KS_PROPERTY = 0x002F0003;
    private const uint KSPROPERTY_TYPE_GET = 1;
    private const uint KSPROPERTY_TYPE_SET = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    private static string? FindTopologyFilter(string cableId)
    {
        string suffix = "\\toporender" + cableId;
        return GetInterfaces(KSCATEGORY_TOPOLOGY).FirstOrDefault(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetProperty(string filterPath, out uint value)
    {
        value = 0;
        using var handle = OpenFilter(filterPath);
        if (handle.IsInvalid)
            return false;

        var prop = new KSPROPERTY { Set = KSPROPSETID_Leyline, Id = KSPROPERTY_LEYLINE_CABLE_CONNECTED, Flags = KSPROPERTY_TYPE_GET };
        return DeviceIoControl(handle, IOCTL_KS_PROPERTY, ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), ref value, sizeof(uint), out _, IntPtr.Zero);
    }

    private static bool TrySetProperty(string filterPath, uint value)
    {
        using var handle = OpenFilter(filterPath);
        if (handle.IsInvalid)
            return false;

        var prop = new KSPROPERTY { Set = KSPROPSETID_Leyline, Id = KSPROPERTY_LEYLINE_CABLE_CONNECTED, Flags = KSPROPERTY_TYPE_SET };
        return DeviceIoControl(handle, IOCTL_KS_PROPERTY, ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), ref value, sizeof(uint), out _, IntPtr.Zero);
    }

    private static SafeFileHandle OpenFilter(string path) =>
        CreateFileW(path, 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3 /* FILE_SHARE_READ | WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

    private static List<string> GetInterfaces(Guid interfaceClass)
    {
        try
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, 0) != 0 || length == 0)
                return [];

            var buffer = new char[length];
            if (CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, 0) != 0)
                return [];

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return [];
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref KSPROPERTY input, uint inputLength,
        ref uint output, uint outputLength, out uint returned, IntPtr overlapped);
}
