using System.Runtime.InteropServices;

namespace MediaCenter.App.Services;

/// <summary>
/// Fragt Windows, ob HDR („Einstellungen → System → Bildschirm → HDR verwenden“) für einen Bildschirm an ist. Nur zur Anzeige:
/// Ob ein Video als HDR ausgegeben wird, entscheidet mpv selbst (target-colorspace-hint).
/// </summary>
internal static class DisplayHdr
{
    private const uint QueryDatabaseActivePaths = 2;
    private const uint GetSourceName = 1;
    private const uint GetAdvancedColorInfo = 9;
    private const uint MonitorDefaultToNearest = 2;
    private const uint AdvancedColorEnabled = 1u << 1;
    private const uint WideColorEnforced = 1u << 2;

    /// <summary>
    /// Ob HDR an dem Bildschirm läuft, auf dem das Fenster liegt (<paramref name="window"/> = 0: auf irgendeinem). null, wenn Windows die Auskunft
    /// verweigert (kein Bildschirm, Fernzugriff) – die Anzeige sagt dann „unbekannt“.
    /// </summary>
    public static bool? IsActive(IntPtr window)
    {
        try
        {
            var device = window == IntPtr.Zero ? null : DeviceNameOf(window);
            var paths = ActivePaths();
            var known = false;
            foreach (var path in paths)
            {
                if (device is not null && SourceName(path) != device) continue;

                var info = new AdvancedColorInfo { Type = GetAdvancedColorInfo, Size = (uint)Marshal.SizeOf<AdvancedColorInfo>(), AdapterId = path.Target.AdapterId, Id = path.Target.Id };
                if (GetDeviceInfo(ref info) != 0) continue;

                known = true;
                // „Erweiterte Farbe“ ohne „Weitfarbraum erzwungen“ ist HDR; mit ist es nur der Weitfarbraum für SDR-Inhalte.
                if ((info.Value & AdvancedColorEnabled) != 0 && (info.Value & WideColorEnforced) == 0) return true;
            }
            return known ? false : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static PathInfo[] ActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QueryDatabaseActivePaths, out var pathCount, out var modeCount) != 0) throw new InvalidOperationException("Bildschirmliste nicht lesbar.");

        var paths = new PathInfo[pathCount];
        var modes = new byte[modeCount * 64]; // DISPLAYCONFIG_MODE_INFO: 64 Byte, der Inhalt wird nicht gebraucht
        if (QueryDisplayConfig(QueryDatabaseActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) throw new InvalidOperationException("Bildschirmliste nicht lesbar.");
        return paths.Take((int)pathCount).ToArray();
    }

    private static string? SourceName(PathInfo path)
    {
        var info = new SourceNameInfo { Type = GetSourceName, Size = (uint)Marshal.SizeOf<SourceNameInfo>(), AdapterId = path.Source.AdapterId, Id = path.Source.Id };
        return GetDeviceInfo(ref info) == 0 ? info.Name : null;
    }

    private static string? DeviceNameOf(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.Device : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceInfo { public Luid AdapterId; public uint Id; public uint ModeIndex; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int Available;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceNameInfo
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topology);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetDeviceInfo(ref AdvancedColorInfo info);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetDeviceInfo(ref SourceNameInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
