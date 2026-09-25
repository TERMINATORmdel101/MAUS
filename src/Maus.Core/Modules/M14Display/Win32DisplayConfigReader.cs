using System.Runtime.InteropServices;

namespace Maus.Core.Modules.M14Display;

/// <summary>
/// Lecture de la configuration d'affichage par <c>QueryDisplayConfig</c>, <c>DisplayConfigGetDeviceInfo</c>,
/// <c>EnumDisplaySettingsExW</c> et <c>EnumDisplayDevicesW</c> (user32.dll). Aucune fonction d'écriture
/// (<c>SetDisplayConfig</c>, <c>ChangeDisplaySettingsEx</c>) n'est déclarée ici.
/// </summary>
internal sealed unsafe partial class Win32DisplayConfigReader : IDisplayConfigReader
{
    private const uint QdcOnlyActivePaths = 0x2;
    private const uint QdcVirtualModeAware = 0x10;
    private const int ErrorInsufficientBuffer = 122;
    private const uint EnumCurrentSettings = unchecked((uint)-1);
    private const uint DisplayInterlaced = 0x2;
    private const int MaxModes = 4096;

    // DISPLAYCONFIG_DEVICE_INFO_TYPE (wingdi.h).
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int GetTargetPreferredMode = 3;
    private const int GetAdapterName = 4;
    private const int GetAdvancedColorInfo = 9;
    private const int GetAdvancedColorInfo2 = 15;

    public IReadOnlyList<DisplayPath>? ReadActivePaths()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static List<DisplayPath>? ReadCore()
    {
        var paths = QueryPaths();
        if (paths is null)
        {
            return null;
        }

        var adapterNames = ReadAdapterNames();
        var result = new List<DisplayPath>();
        foreach (var path in paths)
        {
            var sourceName = ReadSourceName(path);
            var target = ReadTargetName(path);
            var preferred = ReadPreferredMode(path);
            var current = sourceName.Length > 0 ? ReadCurrentMode(sourceName) : null;
            result.Add(new DisplayPath
            {
                MonitorName = target.FriendlyName,
                SourceName = sourceName,
                AdapterDevicePath = ReadAdapterPath(path),
                AdapterName = adapterNames.GetValueOrDefault(sourceName),
                Output = (OutputTechnology)(target.Output ?? path.OutputTechnology),
                Width = current?.Width ?? 0,
                Height = current?.Height ?? 0,
                RefreshHz = path.RefreshDenominator == 0 ? null : (double)path.RefreshNumerator / path.RefreshDenominator,
                NativeWidth = preferred?.Width,
                NativeHeight = preferred?.Height,
                Modes = sourceName.Length > 0 ? ReadModes(sourceName) : [],
                Color = ReadColor(path),
            });
        }

        return result;
    }

    private static PathInfo[]? QueryPaths()
    {
        const uint flags = QdcOnlyActivePaths | QdcVirtualModeAware;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint pathCount;
            uint modeCount;
            if (GetDisplayConfigBufferSizes(flags, &pathCount, &modeCount) != 0)
            {
                return null;
            }

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            int status;
            fixed (PathInfo* pathBuffer = paths)
            fixed (ModeInfo* modeBuffer = modes)
            {
                status = QueryDisplayConfig(flags, &pathCount, pathBuffer, &modeCount, modeBuffer, null);
            }

            if (status == ErrorInsufficientBuffer)
            {
                continue;
            }

            return status == 0 ? paths[..(int)pathCount] : null;
        }

        return null;
    }

    private static string ReadSourceName(in PathInfo path)
    {
        var request = default(SourceNameInfo);
        request.Header = Header(GetSourceName, sizeof(SourceNameInfo), path.SourceLuidLow, path.SourceLuidHigh, path.SourceId);
        return DisplayConfigGetDeviceInfo(&request.Header) == 0 ? new string(request.ViewGdiDeviceName) : string.Empty;
    }

    private static (string FriendlyName, uint? Output) ReadTargetName(in PathInfo path)
    {
        var request = default(TargetNameInfo);
        request.Header = Header(GetTargetName, sizeof(TargetNameInfo), path.TargetLuidLow, path.TargetLuidHigh, path.TargetId);
        return DisplayConfigGetDeviceInfo(&request.Header) == 0
            ? (new string(request.MonitorFriendlyDeviceName).Trim(), request.OutputTechnology)
            : (string.Empty, null);
    }

    private static (int Width, int Height)? ReadPreferredMode(in PathInfo path)
    {
        var request = default(PreferredModeInfo);
        request.Header = Header(GetTargetPreferredMode, sizeof(PreferredModeInfo), path.TargetLuidLow, path.TargetLuidHigh, path.TargetId);
        return DisplayConfigGetDeviceInfo(&request.Header) == 0 && request.Width > 0 && request.Height > 0
            ? ((int)request.Width, (int)request.Height)
            : null;
    }

    private static string? ReadAdapterPath(in PathInfo path)
    {
        var request = default(AdapterNameInfo);
        request.Header = Header(GetAdapterName, sizeof(AdapterNameInfo), path.TargetLuidLow, path.TargetLuidHigh, 0);
        return DisplayConfigGetDeviceInfo(&request.Header) == 0 ? new string(request.AdapterDevicePath) : null;
    }

    /// <summary>Version 2 (build 26100 et plus) si disponible, sinon version 1.</summary>
    private static AdvancedColorInfo? ReadColor(in PathInfo path)
    {
        var v2 = default(AdvancedColorInfo2Packet);
        v2.Header = Header(GetAdvancedColorInfo2, sizeof(AdvancedColorInfo2Packet), path.TargetLuidLow, path.TargetLuidHigh, path.TargetId);
        if (DisplayConfigGetDeviceInfo(&v2.Header) == 0)
        {
            return DisplayParsers.FromAdvancedColorInfo2(v2.Value, v2.ColorEncoding, v2.BitsPerColorChannel, v2.ActiveColorMode);
        }

        var v1 = default(AdvancedColorInfoPacket);
        v1.Header = Header(GetAdvancedColorInfo, sizeof(AdvancedColorInfoPacket), path.TargetLuidLow, path.TargetLuidHigh, path.TargetId);
        return DisplayConfigGetDeviceInfo(&v1.Header) == 0
            ? DisplayParsers.FromAdvancedColorInfo(v1.Value, v1.ColorEncoding, v1.BitsPerColorChannel)
            : null;
    }

    private static (int Width, int Height)? ReadCurrentMode(string sourceName)
    {
        var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        fixed (char* name = sourceName)
        {
            return EnumDisplaySettingsExW(name, EnumCurrentSettings, &mode, 0) != 0
                ? ((int)mode.PelsWidth, (int)mode.PelsHeight)
                : null;
        }
    }

    /// <summary>Modes acceptés par l'écran (sans <c>EDS_RAWMODE</c>), hors modes entrelacés.</summary>
    private static List<DisplayMode> ReadModes(string sourceName)
    {
        var modes = new HashSet<DisplayMode>();
        fixed (char* name = sourceName)
        {
            for (uint index = 0; index < MaxModes; index++)
            {
                var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
                if (EnumDisplaySettingsExW(name, index, &mode, 0) == 0)
                {
                    break;
                }

                if ((mode.DisplayFlags & DisplayInterlaced) == 0 && mode.DisplayFrequency > 1)
                {
                    modes.Add(new DisplayMode((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency));
                }
            }
        }

        return [.. modes];
    }

    /// <summary>Nom de l'adaptateur de chaque source GDI (<c>\\.\DISPLAY1</c> → « NVIDIA GeForce RTX 2080 Ti »).</summary>
    private static Dictionary<string, string> ReadAdapterNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; index < 64; index++)
        {
            var device = new DisplayDevice { Cb = (uint)sizeof(DisplayDevice) };
            if (EnumDisplayDevicesW(null, index, &device, 0) == 0)
            {
                break;
            }

            names.TryAdd(new string(device.DeviceName), new string(device.DeviceString).Trim());
        }

        return names;
    }

    private static DeviceInfoHeader Header(int type, int size, uint luidLow, int luidHigh, uint id) =>
        new() { Type = type, Size = (uint)size, AdapterLuidLow = luidLow, AdapterLuidHigh = luidHigh, Id = id };

    /// <summary><c>DISPLAYCONFIG_PATH_INFO</c> (72 octets) : source (20), cible (48), drapeaux (4).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public uint SourceLuidLow;
        public int SourceLuidHigh;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;
        public uint TargetLuidLow;
        public int TargetLuidHigh;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    /// <summary><c>DISPLAYCONFIG_MODE_INFO</c> (64 octets), lu seulement pour dimensionner le tampon.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
        public ulong Unused;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceNameInfo
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    /// <summary><c>DISPLAYCONFIG_TARGET_DEVICE_NAME</c> (420 octets).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetNameInfo
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    /// <summary><c>DISPLAYCONFIG_TARGET_PREFERRED_MODE</c> (80 octets : le mode cible commence à 32, aligné sur 8).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct PreferredModeInfo
    {
        [FieldOffset(0)]
        public DeviceInfoHeader Header;

        [FieldOffset(20)]
        public uint Width;

        [FieldOffset(24)]
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterNameInfo
    {
        public DeviceInfoHeader Header;
        public fixed char AdapterDevicePath[128];
    }

    /// <summary><c>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO</c> (32 octets).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfoPacket
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    /// <summary><c>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2</c> (36 octets).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo2Packet
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
        public int ActiveColorMode;
    }

    /// <summary><c>DEVMODEW</c> (220 octets), seuls les champs d'affichage sont nommés.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DevMode
    {
        [FieldOffset(68)]
        public ushort Size;

        [FieldOffset(172)]
        public uint PelsWidth;

        [FieldOffset(176)]
        public uint PelsHeight;

        [FieldOffset(180)]
        public uint DisplayFlags;

        [FieldOffset(184)]
        public uint DisplayFrequency;
    }

    /// <summary><c>DISPLAY_DEVICEW</c> (840 octets).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Cb;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, uint* numPathArrayElements, uint* numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, uint* numPathArrayElements, PathInfo* pathArray, uint* numModeInfoArrayElements, ModeInfo* modeInfoArray, void* currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* requestPacket);

    [LibraryImport("user32.dll")]
    private static partial int EnumDisplaySettingsExW(char* deviceName, uint modeNum, DevMode* devMode, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int EnumDisplayDevicesW(char* device, uint devNum, DisplayDevice* displayDevice, uint flags);
}
