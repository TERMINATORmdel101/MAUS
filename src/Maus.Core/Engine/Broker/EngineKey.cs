using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Message du canal privé : les octets exacts d'une demande ou d'une réponse (JSON en UTF-8) et leur signature HMAC-SHA256.
/// Les octets sont signés tels quels, et aucun JSON n'est lu avant la vérification de la signature.
/// </summary>
/// <param name="Key">
/// Seulement dans la présentation de l'interface (premier message d'une connexion) : la clé, que le moteur n'accepte que si
/// son empreinte est celle reçue à son lancement.
/// </param>
public sealed record SignedMessage(byte[] Body, byte[] Mac, byte[]? Key = null);

/// <summary>
/// Clé secrète d'un moteur (anti-rejeu, demande de la certification du Microsoft Store, 29/10/2026) : 32 octets tirés au
/// hasard par l'interface à chaque lancement du moteur. La clé ne passe jamais sur la ligne de commande (lisible par
/// d'autres programmes) : seule son empreinte SHA-256 y passe ; la clé elle-même est présentée une fois par le canal privé,
/// et le moteur ne l'accepte que si elle a cette empreinte. Chaque demande est ensuite signée avec son numéro de séquence,
/// et chaque réponse est liée au numéro de la demande : une demande copiée, rejouée ou modifiée est refusée, une réponse
/// venue d'ailleurs aussi.
/// </summary>
public sealed class EngineKey
{
    /// <summary>Taille de la clé en octets (celle de la sortie de SHA-256).</summary>
    public const int Size = 32;

    /// <summary>Taille d'une signature en octets (sortie de HMAC-SHA256).</summary>
    public const int MacSize = 32;

    private static readonly byte[] RequestDomain = Encoding.ASCII.GetBytes("MAUS-REQ\n");

    private readonly byte[] _secret;

    public EngineKey(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != Size)
        {
            throw new ArgumentException("Clé du moteur de taille invalide.", nameof(secret));
        }

        _secret = (byte[])secret.Clone();
    }

    /// <summary>Nouvelle clé, tirée par le générateur cryptographique de Windows.</summary>
    public static EngineKey Create() => new(RandomNumberGenerator.GetBytes(Size));

    /// <summary>Clé présentée par l'interface : exactement <see cref="Size"/> octets, sinon <c>null</c>.</summary>
    public static EngineKey? Parse(byte[]? secret) => secret is { Length: Size } ? new EngineKey(secret) : null;

    /// <summary>Empreinte lue sur la ligne de commande du moteur : 64 chiffres hexadécimaux minuscules.</summary>
    public static bool IsFingerprint(string? text) =>
        text is { Length: Size * 2 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Empreinte SHA-256 de la clé, pour la ligne de commande du moteur : elle ne permet pas de signer.</summary>
    public string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(_secret));

    /// <summary>Vrai si cette clé a l'empreinte <paramref name="fingerprint"/> (comparaison en temps constant).</summary>
    public bool HasFingerprint(string fingerprint) =>
        fingerprint is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Fingerprint()), Encoding.ASCII.GetBytes(fingerprint));

    /// <summary>Présentation de l'interface au moteur (premier message d'une connexion).</summary>
    public SignedMessage Introduction() => new([], [], (byte[])_secret.Clone());

    public SignedMessage SignRequest(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new SignedMessage(body, Mac(RequestDomain, body));
    }

    public bool VerifyRequest(SignedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Matches(Mac(RequestDomain, message.Body), message.Mac);
    }

    /// <summary>Réponse signée et liée à la demande <paramref name="sequence"/> : elle ne vaut pour aucune autre.</summary>
    public SignedMessage SignResponse(long sequence, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new SignedMessage(body, Mac(ResponseDomain(sequence), body));
    }

    public bool VerifyResponse(long sequence, SignedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Matches(Mac(ResponseDomain(sequence), message.Body), message.Mac);
    }

    private static byte[] ResponseDomain(long sequence) =>
        Encoding.ASCII.GetBytes("MAUS-RES\n" + sequence.ToString(CultureInfo.InvariantCulture) + "\n");

    private byte[] Mac(byte[] domain, byte[] body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _secret);
        hmac.AppendData(domain);
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }

    /// <summary>Comparaison en temps constant : la durée ne révèle pas combien d'octets étaient justes.</summary>
    private static bool Matches(byte[] expected, byte[]? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(expected, actual);
}

/// <summary>
/// Numéros de séquence déjà acceptés par le moteur : chaque numéro ne sert qu'une fois. Les demandes arrivent parfois dans le
/// désordre (plusieurs connexions en même temps) : un numéro plus petit que le plus grand reçu reste accepté s'il est récent
/// et neuf ; plus ancien que la fenêtre, il est refusé (même principe que la fenêtre anti-rejeu d'IPsec, RFC 4303 § 3.4.3).
/// </summary>
public sealed class ReplayWindow(int size = ReplayWindow.DefaultSize)
{
    public const int DefaultSize = 4096;

    private readonly HashSet<long> _seen = [];
    private readonly Lock _gate = new();
    private long _highest;

    /// <returns>Vrai si le numéro est neuf et récent (il est alors retenu), faux s'il a déjà servi, est trop ancien ou n'est pas positif.</returns>
    public bool TryAccept(long sequence)
    {
        lock (_gate)
        {
            if (sequence <= 0 || sequence <= _highest - size || !_seen.Add(sequence))
            {
                return false;
            }

            if (sequence > _highest)
            {
                _highest = sequence;
                _seen.RemoveWhere(s => s <= _highest - size);
            }

            return true;
        }
    }
}
