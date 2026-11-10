using RAMSPDToolkit.I2CSMBus.Interop.PawnIO;
using RAMSPDToolkit.Windows.Driver.Interfaces;

namespace Maus.Core.Workshop.Memory.PawnIo;

/// <summary>
/// Pilote du bus SMBus pour RAMSPDToolkit, par les modules officiels PawnIO (SmbusI801 pour Intel, SmbusPIIX4 pour AMD,
/// SmbusNCT6793 pour certaines cartes), embarqués dans MAUS. Il remplace l'ouverture de la partie « mémoire » de
/// LibreHardwareMonitor, qui relançait sa propre détection des barrettes en arrière-plan et se disputait le bus avec la
/// lecture de MAUS (lecture figée chez le porteur).
/// </summary>
public sealed class SpdBusDriver : IPawnIODriver
{
    private readonly List<PawnIoModule> _modules = [];

    public bool IsOpen => true;

    /// <summary>Raison du dernier refus d'un module par le pilote : la bibliothèque n'en voit qu'un module absent.</summary>
    public string? LastFailure { get; private set; }

    public bool Load() => true;

    public IPawnIOModule? LoadModule(PawnIOSMBusIdentifier pawnIOSMBusIdentifier)
    {
        var file = pawnIOSMBusIdentifier switch
        {
            PawnIOSMBusIdentifier.I801 => "SmbusI801.bin",
            PawnIOSMBusIdentifier.Piix4 => "SmbusPIIX4.bin",
            PawnIOSMBusIdentifier.NCT6793 => "SmbusNCT6793.bin",
            _ => null,
        };
        if (file is null)
        {
            return null;
        }

        try
        {
            var module = PawnIoModule.Load(file);
            _modules.Add(module);
            return new ReadOnlySpdModule(module);
        }
        catch (PawnIoException ex)
        {
            LastFailure = ex.Message;
            return null;
        }
    }

    public void Unload()
    {
        foreach (var module in _modules)
        {
            module.Dispose();
        }

        _modules.Clear();
    }
}

/// <summary>
/// Garde-fou de MAUS devant le module SMBus : les lectures passent, et les seules écritures permises sont le choix de la
/// page d'une puce SPD (adresses 0x36 et 0x37 en DDR4, registre de page MR11 du concentrateur SPD en DDR5), dont la
/// lecture a besoin. Toute autre écriture est refusée avant d'atteindre le pilote : MAUS ne peut ni modifier une puce
/// SPD, ni toucher au contrôleur d'alimentation (PMIC) d'une barrette DDR5. La bibliothèque de lecture peut écrire le
/// registre de page DDR5 avant de savoir ce qu'est la puce : sur une DDR4, ce serait un octet de son contenu. Cette
/// écriture ne passe donc que si la puce s'est d'abord présentée comme un concentrateur SPD DDR5.
/// </summary>
internal sealed class ReadOnlySpdModule(PawnIoModule module) : IPawnIOModule
{
    /// <summary>E_ACCESSDENIED : écriture refusée par MAUS.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    private const long SmbusWrite = 0;
    private const long SmbusRead = 1;
    private const long ByteData = 2;
    private const string Transfer = "ioctl_smbus_xfer";

    public int Execute(string name, long[] inBuffer, uint inSize, long[] outBuffer, uint outSize, out uint returnSize)
    {
        var allowed = name == Transfer
            ? IsAllowedTransfer(inBuffer, inSize)
                && (!WritesHubPage(inBuffer) || IsSpdHub(register => ReadRegister(inBuffer[0], register)))
            : IsAllowedFunction(name);
        if (!allowed)
        {
            returnSize = 0;
            return AccessDenied;
        }

        return module.ExecuteHr(name, inBuffer, inSize, outBuffer, outSize, out returnSize);
    }

    /// <summary>Fonctions du module, autres que le transfert, dont la bibliothèque a besoin pour détecter le bus ; toute autre est refusée.</summary>
    internal static bool IsAllowedFunction(string name) => name is "ioctl_identity" or "ioctl_write_protection" or "ioctl_piix4_port_sel";

    /// <summary>Écriture du registre de page d'une puce DDR5, après <see cref="IsAllowedTransfer"/>.</summary>
    internal static bool WritesHubPage(long[] input) => input[1] == SmbusWrite && input[0] is >= 0x50 and <= 0x57;

    /// <summary>
    /// La puce se présente comme un concentrateur SPD DDR5 : registres MR0 et MR1 (0x51 et 0x18, les valeurs que la bibliothèque
    /// attend aussi). Ces registres ne dépendent pas de la page. Une puce illisible n'en est pas un.
    /// </summary>
    internal static bool IsSpdHub(Func<long, long?> readRegister) => readRegister(0) == 0x51 && readRegister(1) == 0x18;

    private long? ReadRegister(long address, long register)
    {
        long[] input = [address, SmbusRead, register, ByteData, 0, 0, 0, 0, 0];
        var output = new long[1];
        return module.ExecuteHr(Transfer, input, (uint)input.Length, output, (uint)output.Length, out _) == 0 ? output[0] & 0xFF : null;
    }

    /// <summary>
    /// Transfert SMBus du module : [0] adresse, [1] lecture (1) ou écriture (0), [2] commande, [3] protocole, [4] donnée
    /// (PawnIO.Modules, SmbusI801.p, ioctl_smbus_xfer).
    /// </summary>
    internal static bool IsAllowedTransfer(long[] input, uint size)
    {
        if (size < 4 || input.Length < 4)
        {
            return false;
        }

        var (address, readWrite, command, protocol) = (input[0], input[1], input[2], input[3]);
        if (readWrite != SmbusWrite)
        {
            // Les protocoles « appel de procédure » écrivent puis lisent : jamais permis.
            return protocol is not (4 or 7);
        }

        // DDR4 (JEDEC EE1004) : sélection de la page 0 ou 1 par une écriture sans donnée aux adresses 0x36 et 0x37.
        if (address is 0x36 or 0x37)
        {
            return protocol is 0 or 1 or 2;
        }

        // DDR5 (JEDEC SPD5118) : page 0 à 7 choisie dans le registre MR11 (0x0B) du concentrateur SPD, adresses 0x50 à 0x57.
        return address is >= 0x50 and <= 0x57 && command == 0x0B && protocol == ByteData
            && size >= 5 && input.Length >= 5 && input[4] is >= 0 and <= 7;
    }
}
