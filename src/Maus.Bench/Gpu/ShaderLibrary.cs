using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;

namespace Maus.Bench.Gpu;

/// <summary>
/// Shaders HLSL embarqués, compilés par le compilateur de Windows (d3dcompiler_47.dll, modèle 5.0 : DXBC accepté par
/// Direct3D 11 et 12), puis gardés en cache dans %LOCALAPPDATA%\MAUS\bench-shaders (clé = empreinte du source, du point
/// d'entrée, du profil et des définitions). Les #include "x.hlsli" sont remplacés par le fichier embarqué.
/// </summary>
public sealed partial class ShaderLibrary
{
    private static readonly Assembly Self = typeof(ShaderLibrary).Assembly;
    private readonly string _cacheFolder;
    private readonly Dictionary<string, ShaderCode> _compiled = new(StringComparer.Ordinal);

    public ShaderLibrary(string? cacheFolder = null)
    {
        _cacheFolder = cacheFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "bench-shaders");
    }

    /// <summary>Compile (ou reprend du cache) un point d'entrée d'un fichier HLSL embarqué.</summary>
    /// <param name="file">Nom du fichier embarqué (« terrain.hlsl »).</param>
    /// <param name="entry">Point d'entrée (« PSMain »).</param>
    /// <param name="profile">vs_5_0, ps_5_0 ou cs_5_0.</param>
    /// <param name="defines">Définitions de préprocesseur (variantes d'un même shader).</param>
    public ShaderCode Get(string file, string entry, string profile, params (string Name, string Value)[] defines)
    {
        var source = Resolve(file, 0);
        var key = Key(source, entry, profile, defines);
        if (_compiled.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var cachePath = Path.Combine(_cacheFolder, key + ".cso");
        if (File.Exists(cachePath))
        {
            try
            {
                var bytes = File.ReadAllBytes(cachePath);
                if (bytes.Length > 0)
                {
                    return _compiled[key] = new ShaderCode($"{file}:{entry}", bytes);
                }
            }
            catch (IOException)
            {
                // Cache illisible : on recompile.
            }
        }

        var header = new StringBuilder();
        foreach (var (name, value) in defines)
        {
            header.Append("#define ").Append(name).Append(' ').AppendLine(value);
        }

        var code = Compiler.Compile(header + source, entry, file, profile, ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor).ToArray();
        try
        {
            Directory.CreateDirectory(_cacheFolder);
            File.WriteAllBytes(cachePath, code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sans cache, le prochain lancement recompilera : sans conséquence sur la mesure.
        }

        return _compiled[key] = new ShaderCode($"{file}:{entry}", code);
    }

    /// <summary>
    /// Shader compilé pendant la fabrication de MAUS par DXC (modèle 6.5, lancer de rayons) ; <c>null</c> s'il est absent de
    /// cette version (fabriquée sans Windows).
    /// </summary>
    public static ShaderCode? Precompiled(string name)
    {
        using var stream = Self.GetManifestResourceStream("Maus.Bench.Dxil." + name + ".dxil");
        if (stream is null)
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return new ShaderCode(name, bytes);
    }

    /// <summary>Texte d'un fichier embarqué, includes résolus (au plus 8 niveaux).</summary>
    internal static string Resolve(string file, int depth)
    {
        if (depth > 8)
        {
            throw new InvalidOperationException("Inclusions HLSL trop profondes : " + file);
        }

        using var stream = Self.GetManifestResourceStream("Maus.Bench.Shaders." + file)
            ?? throw new FileNotFoundException("Shader embarqué introuvable : " + file);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return IncludeLine().Replace(text, m => Resolve(m.Groups[1].Value, depth + 1));
    }

    private static string Key(string source, string entry, string profile, (string Name, string Value)[] defines)
    {
        var text = new StringBuilder(source).Append('|').Append(entry).Append('|').Append(profile);
        foreach (var (name, value) in defines)
        {
            text.Append('|').Append(name).Append('=').Append(value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    [GeneratedRegex("^\\s*#include\\s+\"([^\"]+)\"\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IncludeLine();
}
