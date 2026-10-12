using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maus.Core.Engine.Broker;

/// <summary>Une demande de l'interface au moteur : une opération de la liste fermée, ses paramètres, la langue de l'interface.</summary>
public sealed record EngineRequest(string Op, string Language, JsonElement? Args = null);

/// <summary>
/// Une réponse du moteur : « progress » (un module de l'audit terminé), « result » (la réponse) ou « error » (l'opération
/// a échoué, message à afficher).
/// </summary>
public sealed record EngineResponse(string Kind, JsonElement? Payload = null, string? Error = null);

/// <summary>
/// Format des messages du canal privé entre l'interface et le moteur : 4 octets de longueur (petit-boutiste), puis le JSON
/// en UTF-8. Taille bornée (un message plus long est refusé, jamais lu en entier).
/// </summary>
public static partial class EngineProtocol
{
    /// <summary>Au-delà, le message est refusé : un audit complet tient en quelques centaines de kilo-octets.</summary>
    public const int MaxMessageBytes = 32 << 20;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("Message du moteur trop long.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>Le message, ou <c>null</c> si l'autre côté a fermé le canal.</returns>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException("Longueur de message du moteur invalide.");
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Message du moteur coupé.");
        }

        return JsonSerializer.Deserialize<T>(body, Json);
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
public sealed class EngineException(string message, Exception? inner = null) : Exception(message, inner);
