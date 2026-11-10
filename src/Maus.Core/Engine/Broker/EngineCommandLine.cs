using System.Globalization;
using System.Text.RegularExpressions;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Ce que le processus moteur reçoit de la ligne de commande, validé avant toute autre action : un nom de canal, le numéro du
/// processus de l'interface et l'empreinte de la clé de signature des messages. Rien d'autre n'est accepté.
/// </summary>
public static partial class EngineCommandLine
{
    /// <returns><c>true</c> si les trois arguments ont exactement la forme attendue.</returns>
    public static bool TryParse(string pipe, string clientPid, string keyFingerprint, out int pid)
    {
        pid = 0;
        return PipeName().IsMatch(pipe)
            && int.TryParse(clientPid, NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && pid > 0
            && EngineKey.IsFingerprint(keyFingerprint);
    }

    // \z et non $ : $ accepterait un saut de ligne final.
    [GeneratedRegex(@"\AMAUS-[0-9a-f]{32}\z")]
    private static partial Regex PipeName();
}
