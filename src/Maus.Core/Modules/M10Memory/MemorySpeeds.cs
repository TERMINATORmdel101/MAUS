namespace Maus.Core.Modules.M10Memory;

/// <summary>Types SMBIOS, vitesses JEDEC et conversions de vitesse de la mémoire vive.</summary>
internal static class MemorySpeeds
{
    /// <summary>Écart toléré entre deux vitesses (Windows arrondit parfois 2666 en 2667).</summary>
    public const int Tolerance = 10;

    private static readonly int[] JedecDdr3 = [800, 1066, 1333, 1600, 1866, 2133];
    private static readonly int[] JedecDdr4 = [2133, 2400, 2666, 2933, 3200];
    private static readonly int[] JedecDdr5 = [4000, 4400, 4800, 5200, 5600, 6400];

    /// <summary>Latence CAS minimale des modules JEDEC courants (DDR4 : bins rapides ; DDR5 : bins « B » du commerce).</summary>
    private static readonly Dictionary<int, int> MinJedecCasDdr4 = new() { [2133] = 15, [2400] = 16, [2666] = 17, [2933] = 19, [3200] = 20 };
    private static readonly Dictionary<int, int> MinJedecCasDdr5 = new() { [4800] = 40, [5200] = 42, [5600] = 46, [6000] = 48, [6400] = 52 };

    /// <summary>Nom du type SMBIOS (<c>SMBIOSMemoryType</c>), ou <c>null</c> s'il n'est pas courant.</summary>
    public static string? TypeName(long? smbiosType) => smbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    /// <summary>Génération DDR (3, 4 ou 5) d'une mémoire en barrettes ; <c>null</c> pour la LPDDR soudée ou un type inconnu.</summary>
    public static int? Generation(long? smbiosType) => smbiosType switch
    {
        24 => 3,
        26 => 4,
        34 => 5,
        _ => null,
    };

    public static bool IsLowPower(long? smbiosType) => smbiosType is 27 or 28 or 29 or 30 or 35;

    public static bool Same(int a, int b) => Math.Abs(a - b) <= Tolerance;

    public static bool IsJedecSpeed(int generation, int speed) => JedecSpeeds(generation).Any(s => Same(s, speed));

    /// <summary>Vitesse JEDEC la plus haute : au-delà, un profil XMP/EXPO ou un réglage manuel est forcément actif.</summary>
    public static int? MaxJedecSpeed(int generation) => JedecSpeeds(generation) is { Length: > 0 } speeds ? speeds[^1] : null;

    /// <summary>
    /// Les vieux BIOS (SMBIOS antérieur à 3.1) renvoient la fréquence d'horloge en MHz, soit la moitié des MT/s :
    /// une DDR4 à moins de 2000 ou une DDR5 à 3400 ou moins est donc convertie.
    /// </summary>
    public static int ToTransfersPerSecond(int? generation, int speed) => generation switch
    {
        4 when speed is > 0 and < 2000 => speed * 2,
        5 when speed is > 0 and <= 3400 => speed * 2,
        _ => speed,
    };

    /// <summary>Latence CAS compatible avec un module JEDEC (pas de profil XMP/EXPO nécessaire pour cette vitesse).</summary>
    public static bool IsJedecTiming(int? generation, int speed, int? casLatency)
    {
        if (casLatency is null)
        {
            return false;
        }

        var table = generation switch
        {
            4 => MinJedecCasDdr4,
            5 => MinJedecCasDdr5,
            _ => null,
        };
        var bin = table?.Keys.FirstOrDefault(s => Same(s, speed));
        return bin is > 0 && casLatency >= table![bin.Value];
    }

    /// <summary>Vitesse codée sur deux chiffres (Kingston « KF432 », Crucial « 32 », « 26 » pour 2666…).</summary>
    public static int FromTwoDigits(int code) => code switch
    {
        13 => 1333,
        18 => 1866,
        21 => 2133,
        26 => 2666,
        29 => 2933,
        34 => 3466,
        37 => 3733,
        41 => 4133,
        42 => 4266,
        _ => code * 100,
    };

    /// <summary>Vitesse codée sur trois chiffres (Patriot « 320 », Crucial « 266 »).</summary>
    public static int FromThreeDigits(int code) => code switch
    {
        133 => 1333,
        186 => 1866,
        213 => 2133,
        266 => 2666,
        293 => 2933,
        346 => 3466,
        373 => 3733,
        413 => 4133,
        426 => 4266,
        _ => code * 10,
    };

    /// <summary>Vitesse codée sur deux ou trois chiffres selon la longueur.</summary>
    public static int FromDigits(string digits) => digits.Length == 3
        ? FromThreeDigits(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture))
        : FromTwoDigits(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Génération probable d'après la vitesse, quand la référence ne l'indique pas.</summary>
    public static int GuessGeneration(int speed) => speed switch
    {
        >= 4000 => 5,
        >= 2133 => 4,
        _ => 3,
    };

    private static int[] JedecSpeeds(int generation) => generation switch
    {
        3 => JedecDdr3,
        4 => JedecDdr4,
        5 => JedecDdr5,
        _ => [],
    };
}
