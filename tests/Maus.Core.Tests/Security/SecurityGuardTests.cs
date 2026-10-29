using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Maus.Core.Platform;

namespace Maus.Core.Tests.Security;

/// <summary>
/// Garde-fou de sécurité (directives du porteur, 27/10/2026 : moindre privilège, pas de gestion manuelle de la mémoire, pas de
/// <c>system()</c>, pas de fonctions C sans limites). Chaque pratique sensible n'est permise que dans les fichiers déjà relus
/// un par un. Un nouveau fichier qui en a besoin fait échouer ce test : le relire (tailles vérifiées, mémoire rendue dans un
/// <c>finally</c>, arguments construits par MAUS et filtrés), puis l'ajouter à la liste.
/// </summary>
public partial class SecurityGuardTests
{
    /// <summary>Lancement d'un programme : chemin de System32 (ou de MAUS), arguments séparés ou filtrés.</summary>
    private static readonly string[] ProcessLaunchers =
    [
        "Maus.App/EngineHost.cs", "Maus.App/ShellLauncher.cs", "Maus.Core/Engine/Broker/LocalMausEngine.Workshop.cs",
        "Maus.Core/Engine/TaskSchedulerClient.cs", "Maus.Core/Fixes/ScheduledTasks.cs", "Maus.Core/Platform/ReadOnlyCommandRunner.cs",
        "Maus.Core/Platform/VisibleConsole.cs", "Maus.Core/Workshop/PresentMonCapture.cs",
    ];

    /// <summary>Mémoire réservée à la main (Marshal, NativeMemory), toujours rendue dans un <c>finally</c>.</summary>
    private static readonly string[] ManualAllocations =
    [
        "Maus.Bench/Platform/BenchWindow.cs", "Maus.Core/Modules/M01Audit/NativeReaders.cs", "Maus.Core/Platform/SettingChangeNotifier.cs",
        "Maus.Core/Workshop/DiskSpeedTest.cs", "Maus.Core/Workshop/EtwDriverLatencyTracer.cs", "Maus.Core/Workshop/MemoryTest.cs",
        "Maus.Core/Workshop/Pdh.cs",
    ];

    /// <summary>Copies de mémoire brute (équivalent de <c>memcpy</c>), avec la taille de la destination passée et vérifiée.</summary>
    private static readonly string[] RawCopies =
    [
        "Maus.Bench/Gpu/D3D11/D3D11CommandList.cs", "Maus.Bench/Gpu/D3D12/D3D12CommandList.cs", "Maus.Bench/Gpu/D3D12/D3D12GpuDevice.cs",
        "Maus.Core/Workshop/MemoryTest.cs",
    ];

    /// <summary>Code <c>unsafe</c> (pointeurs) : appels de Windows, de la carte graphique et des pilotes, relus un par un.</summary>
    private static readonly string[] UnsafeFiles =
    [
        "Maus.Bench/Gpu/D3D11/D3D11CommandList.cs", "Maus.Bench/Gpu/D3D11/D3D11GpuDevice.cs", "Maus.Bench/Gpu/D3D11/D3D11Resources.cs",
        "Maus.Bench/Gpu/D3D12/D3D12CommandList.cs", "Maus.Bench/Gpu/D3D12/D3D12GpuDevice.cs", "Maus.Bench/Gpu/D3D12/D3D12RayTracing.cs",
        "Maus.Bench/Gpu/D3D12/D3D12Upload.cs", "Maus.Bench/Platform/BenchWindow.cs", "Maus.Core/Modules/M01Audit/NativeReaders.cs",
        "Maus.Core/Modules/M05Power/Win32PowerPlatform.cs", "Maus.Core/Modules/M08Bios/FirmwareVariableReader.cs",
        "Maus.Core/Modules/M09Gpu/GpuSchedulingReader.cs", "Maus.Core/Modules/M14Display/RefreshRateTrial.cs",
        "Maus.Core/Modules/M14Display/Win32DisplayConfigReader.cs", "Maus.Core/Modules/M18Network/WifiSource.cs",
        "Maus.Core/Platform/DriverDateSource.cs", "Maus.Core/Workshop/D3D11GpuMemory.cs", "Maus.Core/Workshop/DiskSpeedTest.cs",
        "Maus.Core/Workshop/EtwDriverLatencyTracer.cs", "Maus.Core/Workshop/GpuPerformance.cs", "Maus.Core/Workshop/Memory/PawnIo/PawnIoModule.cs",
        "Maus.Core/Workshop/MemoryTest.cs", "Maus.Core/Workshop/Nvml.cs", "Maus.Core/Workshop/Pdh.cs", "Maus.Core/Workshop/WindowsCoreTopology.cs",
    ];

    /// <summary>Modèles de texte à trous : seulement des textes écrits dans le code (traduits), jamais une saisie.</summary>
    private static readonly string[] FormatCallers =
    [
        "Maus.App/ViewModels/MainViewModel.Home.cs", "Maus.App/ViewModels/ModuleViewModel.cs", "Maus.Core/Localization/Texts.cs",
    ];

    private static string SourceFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src"));

    private static Dictionary<string, string> Sources()
    {
        var root = SourceFolder();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
    }

    private static List<string> FilesMatching(Regex pattern) =>
        [.. Sources().Where(s => pattern.IsMatch(s.Value)).Select(s => s.Key).Order(StringComparer.Ordinal)];

    private static void OnlyIn(Regex pattern, string[] allowed, string what)
    {
        var found = FilesMatching(pattern);
        Assert.True(found.Count > 0, $"{what} : aucun fichier trouvé, le motif de recherche est sans doute faux");
        var unexpected = found.Except(allowed, StringComparer.Ordinal).ToList();
        Assert.True(unexpected.Count == 0, $"{what} hors des fichiers relus : {string.Join(", ", unexpected)}");
    }

    [Fact]
    public void Sources_are_found() => Assert.True(Sources().Count > 300);

    [Fact]
    public void Programs_are_started_only_from_reviewed_files() =>
        OnlyIn(ProcessStart(), ProcessLaunchers, "Lancement de programme");

    [Fact]
    public void Shell_execution_and_command_interpreter_stay_where_they_were_reviewed()
    {
        OnlyIn(ShellExecute(), ["Maus.App/EngineHost.cs"], "UseShellExecute = true");

        // « cmd.exe » n'est lancé que par VisibleConsole ; le classement des exclusions Defender ne fait que citer son nom.
        OnlyIn(CommandInterpreter(), ["Maus.Core/Platform/VisibleConsole.cs", "Maus.Core/Modules/M01Audit/DefenderExclusionClassifier.cs"], "cmd.exe");
    }

    [Fact]
    public void Manual_memory_is_reserved_only_in_reviewed_files_and_always_given_back_in_finally()
    {
        OnlyIn(ManualAllocation(), ManualAllocations, "Mémoire réservée à la main");
        var sources = Sources();
        foreach (var file in FilesMatching(ManualAllocation()))
        {
            Assert.True(sources[file].Contains("finally", StringComparison.Ordinal) && FreeCall().IsMatch(sources[file]),
                $"{file} réserve de la mémoire sans la rendre dans un bloc finally");
        }
    }

    [Fact]
    public void Raw_memory_copies_and_unsafe_code_stay_in_reviewed_files()
    {
        OnlyIn(RawCopy(), RawCopies, "Copie de mémoire brute");
        OnlyIn(UnsafeKeyword(), UnsafeFiles, "Code unsafe");
    }

    [Fact]
    public void C_runtime_functions_without_bounds_are_never_imported() =>
        Assert.Empty(FilesMatching(CRuntimeImport()));

    [Fact]
    public void Format_templates_are_only_formatted_where_they_are_written_in_the_code() =>
        OnlyIn(FormatCall(), FormatCallers, "string.Format");

    /// <summary>
    /// Apparence (0.8.4) : aucun XAML, thème ou « skin » lu depuis le disque ou téléchargé (XamlReader peut exécuter du code
    /// d'un fichier), aucune image ni ressource prise sur Internet ou à côté de l'exécutable (pack://siteoforigin).
    /// </summary>
    [Fact]
    public void Interface_never_loads_xaml_or_images_from_outside_maus()
    {
        Assert.Empty(FilesMatching(XamlLoader()));
        var root = SourceFolder();
        var xaml = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(xaml);
        var outside = xaml.Where(f => RemoteResource().IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.True(outside.Count == 0, "Ressource venue de l'extérieur : " + string.Join(", ", outside));
    }

    [Fact]
    public void Every_settings_page_written_in_maus_is_accepted_by_the_launcher()
    {
        var root = SourceFolder();
        var texts = Sources().Values.Concat(Directory.EnumerateFiles(Path.Combine(root, "Maus.Core", "Catalog"), "*.json").Select(File.ReadAllText));
        var pages = texts.SelectMany(t => SettingsPageLiteral().Matches(t).Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToList();

        Assert.True(pages.Count > 20);
        Assert.All(pages, page => Assert.True(SettingsPages.IsValid(page), page));
    }

    [Theory]
    [InlineData("ms-settings:display", true)]
    [InlineData("ms-settings:windowsupdate-optionalupdates", true)]
    [InlineData("ms-settings:", true)]
    [InlineData("ms-settings:display \"C:\\autre.exe\"", false)]
    [InlineData("ms-settings:display /select,C:\\", false)]
    [InlineData("ms-settings:Display", false)]
    [InlineData("https://example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Settings_pages_with_spaces_quotes_or_other_characters_are_refused(string? page, bool valid) =>
        Assert.Equal(valid, SettingsPages.IsValid(page));

    [GeneratedRegex(@"Process\.Start\(|new ProcessStartInfo")]
    private static partial Regex ProcessStart();

    [GeneratedRegex(@"UseShellExecute\s*=\s*true")]
    private static partial Regex ShellExecute();

    [GeneratedRegex("\"cmd\\.exe\"", RegexOptions.IgnoreCase)]
    private static partial Regex CommandInterpreter();

    [GeneratedRegex(@"Marshal\.(AllocHGlobal|AllocCoTaskMem|StringToHGlobal\w*|StringToCoTaskMem\w*)\(|NativeMemory\.(Alloc|AlignedAlloc|AllocZeroed)\(")]
    private static partial Regex ManualAllocation();

    [GeneratedRegex(@"Marshal\.(FreeHGlobal|FreeCoTaskMem)\(|NativeMemory\.(Free|AlignedFree)\(")]
    private static partial Regex FreeCall();

    [GeneratedRegex(@"Buffer\.MemoryCopy\(|Unsafe\.(CopyBlock|CopyBlockUnaligned|InitBlock|InitBlockUnaligned)\(|NativeMemory\.(Copy|Fill|Clear)\(")]
    private static partial Regex RawCopy();

    [GeneratedRegex(@"\bunsafe\b")]
    private static partial Regex UnsafeKeyword();

    /// <summary>Bibliothèque C (msvcrt, ucrtbase) ou fonctions de chaînes sans limite de kernel32 (lstrcpy, lstrcat, wsprintf).</summary>
    [GeneratedRegex(@"(DllImport|LibraryImport)\(\s*""(msvcrt|ucrtbase|api-ms-win-crt)[^""]*""|EntryPoint\s*=\s*""(lstrcpy|lstrcat|wsprintf|strcpy|strcat|sprintf|gets|system)\w*""", RegexOptions.IgnoreCase)]
    private static partial Regex CRuntimeImport();

    [GeneratedRegex(@"\b[Ss]tring\.Format\(")]
    private static partial Regex FormatCall();

    [GeneratedRegex(@"XamlReader\.(Load|Parse)|XamlServices\.(Load|Parse)")]
    private static partial Regex XamlLoader();

    [GeneratedRegex(@"(Source|ImageSource|UriSource)\s*=\s*""(https?:|file:|pack://siteoforigin)", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteResource();

    [GeneratedRegex("\"(ms-settings:[^\"]*)\"")]
    private static partial Regex SettingsPageLiteral();
}
