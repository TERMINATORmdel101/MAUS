using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Une demande de l'interface au moteur : une opération de la liste fermée, ses paramètres, la langue de l'interface, et son
/// numéro de séquence (unique, signé avec la demande : voir <see cref="EngineKey"/> et <see cref="ReplayWindow"/>).
/// </summary>
public sealed record EngineRequest(string Op, string Language, JsonElement? Args = null, long Sequence = 0);

/// <summary>
/// Une réponse du moteur : « progress » (un module de l'audit terminé), « result » (la réponse) ou « error » (l'opération
/// a échoué, message à afficher).
/// </summary>
public sealed record EngineResponse(string Kind, JsonElement? Payload = null, string? Error = null);

/// <summary>
/// Format des messages du canal privé entre l'interface et le moteur : 4 octets de longueur (petit-boutiste), 1 octet de type,
/// puis soit la clé (présentation), soit 32 octets de signature suivis du corps JSON en UTF-8. Aucun JSON n'est lu ici : le corps
/// n'est analysé qu'une fois sa signature vérifiée. Taille bornée (un message plus long est refusé, jamais lu en entier).
/// </summary>
public static partial class EngineProtocol
{
    /// <summary>Au-delà, le message est refusé : un audit complet tient en quelques centaines de kilo-octets.</summary>
    public const int MaxMessageBytes = 32 << 20;

    private const byte KeyMessage = 1;
    private const byte SignedBody = 2;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Sans cela, chaque guillemet et chaque accent coûteraient six octets ou plus. « Unsafe » ne vise que le code HTML, et
        // ce texte ne va jamais dans une page.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // Par leur nom seulement : un nombre pourrait désigner une valeur hors de l'énumération.
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static async Task WriteAsync(Stream stream, SignedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var (kind, first, second) = message.Key is { } key ? (KeyMessage, key, Array.Empty<byte>()) : (SignedBody, message.Mac, message.Body);
        var length = 1L + first.Length + second.Length;
        if (length > MaxMessageBytes)
        {
            throw new InvalidDataException(T("Message du moteur trop long."));
        }

        var header = new byte[5 + first.Length];
        BinaryPrimitives.WriteInt32LittleEndian(header, (int)length);
        header[4] = kind;
        first.CopyTo(header, 5);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (second.Length > 0)
        {
            await stream.WriteAsync(second, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Une signature trop courte est rendue telle quelle, comme une clé de mauvaise taille : c'est leur vérification qui les
    /// refuse, et le refus compte (<see cref="EngineServer.MaxRefusals"/>).
    /// </summary>
    /// <returns>Le message, ou <c>null</c> si l'autre côté a fermé le canal.</returns>
    public static async Task<SignedMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException(T("Longueur de message du moteur invalide."));
        }

        var message = new byte[length];
        if (!await ReadExactAsync(stream, message, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException(T("Message du moteur coupé."));
        }

        return message[0] switch
        {
            KeyMessage => new SignedMessage([], [], message[1..]),
            SignedBody when length > EngineKey.MacSize => new SignedMessage(message[(1 + EngineKey.MacSize)..], message[1..(1 + EngineKey.MacSize)]),
            SignedBody => new SignedMessage([], message[1..]),
            _ => throw new InvalidDataException(T("Longueur de message du moteur invalide.")),
        };
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    public static T? FromElement<T>(JsonElement? element) =>
        element is { } value ? value.Deserialize<T>(Json) : default;

    /// <summary>Processus à l'autre bout d'un canal nommé (Microsoft Learn, GetNamedPipeClientProcessId / ServerProcessId).</summary>
    public static int? ClientProcessId(SafeHandle pipe) => GetNamedPipeClientProcessId(pipe, out var id) ? (int)id : null;

    public static int? ServerProcessId(SafeHandle pipe) => GetNamedPipeServerProcessId(pipe, out var id) ? (int)id : null;

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint processId);
}

/// <summary>Le moteur a refusé ou n'a pas pu faire l'opération (message à afficher), ou il ne répond plus.</summary>
public class EngineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Le canal du moteur n'a pas pu être ouvert : rien n'a été envoyé (moteur arrêté, ou pas encore prêt).</summary>
public sealed class EngineUnavailableException(string message, Exception? inner = null) : EngineException(message, inner);
