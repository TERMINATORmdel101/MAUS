using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Date du pilote installé pour un périphérique (remplaçable dans les tests).</summary>
public interface IDriverDateSource
{
    /// <summary>Date du pilote, ou <c>null</c> si le périphérique est introuvable ou la propriété absente.</summary>
    DateTime? DriverDate(string instanceId);
}

/// <summary>
/// Date du pilote telle que l'affiche le Gestionnaire de périphériques : propriété <c>DEVPKEY_Device_DriverDate</c>
/// (FILETIME, clé {A8B865DD-2E3D-4094-AD97-E593A70C75D6} 2), lue par <c>CM_Locate_DevNodeW</c> et
/// <c>CM_Get_DevNode_PropertyW</c> (cfgmgr32, fonctions documentées de lecture seule).
/// La date de la classe WMI <c>Win32_PnPSignedDriver</c> n'est pas utilisée : sur le PC du porteur (29/09/2026), elle
/// inversait le jour et le mois (Intel : 11/05/2025 au lieu du 05/11/2025, HyperX : une date dans le futur).
/// </summary>
public sealed unsafe partial class CfgMgrDriverDateSource : IDriverDateSource
{
    private const uint Success = 0;
    private const uint LocateNormal = 0;
    private const uint LocatePhantom = 1;
    private const uint FileTimeType = 0x10;

    private static readonly DevPropKey DriverDateKey = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 2);

    public DateTime? DriverDate(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        try
        {
            if (CM_Locate_DevNodeW(out var node, instanceId, LocateNormal) != Success
                && CM_Locate_DevNodeW(out node, instanceId, LocatePhantom) != Success)
            {
                return null;
            }

            var key = DriverDateKey;
            long fileTime = 0;
            uint type = 0;
            uint size = sizeof(long);
            if (CM_Get_DevNode_PropertyW(node, &key, &type, (byte*)&fileTime, &size, 0) != Success || type != FileTimeType || size != sizeof(long) || fileTime <= 0)
            {
                return null;
            }

            // La date est enregistrée à minuit UTC : on garde le jour, sans décalage horaire.
            return DateTime.FromFileTimeUtc(fileTime).Date;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid category, uint id)
    {
        public readonly Guid Category = category;
        public readonly uint Id = id;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint devInst, DevPropKey* propertyKey, uint* propertyType, byte* buffer, uint* bufferSize, uint flags);
}
