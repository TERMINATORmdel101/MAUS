using System.Text.Json;
using Maus.Bench;
using Maus.Core.Engine.Broker;
using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Workshop;

/// <summary>
/// Benchmark lancé par le moteur (0.8.6) : il garde ses droits administrateur mais n'écrit rien dans le profil de l'utilisateur.
/// Sa passe et son image passent par un dossier de sortie réservé, relues et vérifiées par le moteur, puis gardées par la
/// fenêtre, sans droits. Aucun vrai benchmark n'est lancé ici.
/// </summary>
public sealed class BenchmarkOutputTests : IDisposable
{
    private const string Root = @"C:\ProgramData\MAUS\benchmark";
    private const string Guid32 = "0123456789abcdef0123456789abcdef";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private readonly string _folder = Directory.CreateTempSubdirectory("maus-sortie-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static BenchmarkReport Pass(double gpuScore = 9000, bool withTests = true) => new()
    {
        Date = DateTimeOffset.Now.AddMinutes(-1),
        Edition = BenchmarkScoring.CurrentEdition,
        Api = "Direct3D 12",
        Gpu = "Carte graphique d'essai",
        Cpu = "Processeur d'essai",
        Threads = 16,
        RenderResolution = "1920×1080",
        DisplayResolution = "2560×1440",
        Tests = withTests ? [new BenchmarkTestResult("ring", "gpu", 120, "img/s", 9000)] : [],
        GpuScore = gpuScore,
        Completed = true,
        Image = Root + @"\" + Guid32 + @"\share.png",
    };

    private string Output(string name) => Path.Combine(_folder, name);

    private BenchmarkHistoryStore Store() => new(Path.Combine(_folder, "profil", "benchmark-history.json"), _ => { });

    private static Func<BenchmarkHistoryStore> NeverOpened() => () => throw new InvalidOperationException("l'historique du profil a été ouvert");

    // ---------- chemin de --output

    [Theory]
    [InlineData(Root + @"\" + Guid32, true)]
    [InlineData(@"c:\programdata\maus\benchmark\" + Guid32, true)]
    [InlineData(Root, false)]
    [InlineData(Root + @"\", false)]
    [InlineData(Root + @"\" + Guid32 + @"\", false)]
    [InlineData(Root + @"\" + Guid32 + @"\sous-dossier", false)]
    [InlineData(Root + @"\..\journal\" + Guid32, false)]
    [InlineData(Root + @"\.\" + Guid32, false)]
    [InlineData(Root + @"\pas-un-guid", false)]
    [InlineData(Root + @"\{01234567-89ab-cdef-0123-456789abcdef}", false)]
    [InlineData(Root + @"\" + Guid32 + ":flux", false)]
    [InlineData(@"\\?\C:\ProgramData\MAUS\benchmark\" + Guid32, false)]
    [InlineData(@"\\serveur\partage\MAUS\benchmark\" + Guid32, false)]
    [InlineData("C:/ProgramData/MAUS/benchmark/" + Guid32, false)]
    [InlineData(@"C:\Users\Alex\AppData\Local\MAUS\benchmark\" + Guid32, false)]
    [InlineData(@"MAUS\benchmark\" + Guid32, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_folder_made_by_the_engine_is_accepted_as_output(string? path, bool accepted) =>
        Assert.Equal(accepted, BenchmarkOutput.IsValidFolder(path, Root));

    [Fact]
    public void Output_option_is_read_and_checked_against_the_reserved_folder()
    {
        var folder = Path.Combine(BenchmarkOutput.Root, Guid.NewGuid().ToString("N"));

        Assert.True(BenchOptions.TryParse(["--output", folder], elevated: true, out var options, out _));
        Assert.Equal(folder, options!.OutputFolder);
        Assert.False(BenchOptions.TryParse(["--output", _folder], elevated: true, out _, out var refused));
        Assert.False(string.IsNullOrEmpty(refused));
        Assert.False(BenchOptions.TryParse(["--output"], elevated: false, out _, out _));
    }

    [Theory]
    [InlineData("--result")]
    [InlineData("--screenshots")]
    [InlineData("--capture")]
    public void With_administrator_rights_or_an_output_folder_nothing_is_written_elsewhere(string option)
    {
        var elsewhere = Output("ailleurs");
        var output = Path.Combine(BenchmarkOutput.Root, Guid.NewGuid().ToString("N"));

        Assert.False(BenchOptions.TryParse([option, elsewhere], elevated: true, out _, out var refused));
        Assert.Contains(option, refused, StringComparison.Ordinal);
        Assert.False(BenchOptions.TryParse(["--output", output, option, elsewhere], elevated: false, out _, out _));
        Assert.True(BenchOptions.TryParse([option, elsewhere], elevated: false, out _, out _));
    }

    // ---------- côté benchmark : où va la passe

    [Fact]
    public void With_an_output_folder_the_pass_goes_there_and_nowhere_else()
    {
        var recorder = new BenchmarkRecorder(_folder, elevated: true, trial: false, screenshotFolder: null, NeverOpened());

        Assert.Equal(BenchmarkRecord.Kept, recorder.Record(Pass()));
        Assert.Equal(Output(BenchmarkOutput.ImageFileName), recorder.ImagePathFor(DateTimeOffset.Now));
        Assert.Equal([BenchmarkOutput.ReportFileName], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void A_missing_output_folder_is_never_created_again()
    {
        var gone = Output(Guid32);
        var recorder = new BenchmarkRecorder(gone, elevated: true, trial: false, screenshotFolder: null, NeverOpened());

        Assert.Equal(BenchmarkRecord.Failed, recorder.Record(Pass()));
        Assert.Null(recorder.ImagePathFor(DateTimeOffset.Now));
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public void With_administrator_rights_and_no_output_folder_the_profile_is_never_opened()
    {
        var recorder = new BenchmarkRecorder(null, elevated: true, trial: false, screenshotFolder: _folder, NeverOpened());

        Assert.Equal(BenchmarkRecord.NotKept, recorder.Record(Pass()));
        Assert.Null(recorder.ImagePathFor(DateTimeOffset.Now));
        Assert.Null(new BenchmarkRecorder(null, elevated: true, trial: true, screenshotFolder: _folder, NeverOpened()).ImagePathFor(DateTimeOffset.Now));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public void Without_rights_the_benchmark_keeps_its_pass_in_the_history_as_before()
    {
        var store = Store();
        var pass = Pass();
        var recorder = new BenchmarkRecorder(null, elevated: false, trial: false, screenshotFolder: null, () => store);

        Assert.Equal(BenchmarkRecord.Kept, recorder.Record(pass));
        Assert.Single(store.Load());
        Assert.Equal(store.ImagePathFor(pass.Date), recorder.ImagePathFor(pass.Date));
    }

    [Fact]
    public void A_trial_pass_is_never_kept()
    {
        var recorder = new BenchmarkRecorder(null, elevated: false, trial: true, screenshotFolder: _folder, NeverOpened());

        Assert.Equal(BenchmarkRecord.Skipped, recorder.Record(Pass()));
        Assert.Equal(Output("share.png"), recorder.ImagePathFor(DateTimeOffset.Now));
    }

    [Fact]
    public void The_pass_file_is_never_written_over_an_existing_one()
    {
        BenchmarkOutput.WriteReport(_folder, Pass());

        Assert.Throws<IOException>(() => BenchmarkOutput.WriteReport(_folder, Pass()));
    }

    // ---------- côté moteur : relecture bornée et vérifiée

    [Fact]
    public void The_engine_reads_back_a_valid_pass_and_its_image_then_deletes_the_folder()
    {
        var folder = Directory.CreateDirectory(Output(Guid32)).FullName;
        BenchmarkOutput.WriteReport(folder, Pass());
        File.WriteAllBytes(Path.Combine(folder, BenchmarkOutput.ImageFileName), Png);

        var outcome = BenchmarkOutput.Read(folder, 5);
        BenchmarkOutput.Delete(folder);

        Assert.Equal(5, outcome.ExitCode);
        Assert.Equal(9000, Assert.Single(outcome.Report!.Tests).Score);
        Assert.Null(outcome.Report.Image);
        Assert.Equal(Png, outcome.Image);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_damaged_pass_is_not_returned_and_neither_is_its_image()
    {
        File.WriteAllText(Output(BenchmarkOutput.ReportFileName), "{ ce n'est pas du JSON");
        File.WriteAllBytes(Output(BenchmarkOutput.ImageFileName), Png);

        var outcome = BenchmarkOutput.Read(_folder, 0);

        Assert.Null(outcome.Report);
        Assert.Null(outcome.Image);
    }

    [Fact]
    public void An_impossible_or_empty_pass_is_refused()
    {
        Assert.Null(BenchmarkOutput.ParseReport(JsonSerializer.SerializeToUtf8Bytes(Pass(gpuScore: 1e9), BenchmarkHistoryStore.Json)));
        Assert.Null(BenchmarkOutput.ParseReport(JsonSerializer.SerializeToUtf8Bytes(Pass(withTests: false), BenchmarkHistoryStore.Json)));
        Assert.Null(BenchmarkOutput.ParseReport(JsonSerializer.SerializeToUtf8Bytes(Pass() with { Date = DateTimeOffset.Now.AddDays(3) }, BenchmarkHistoryStore.Json)));
        Assert.Null(BenchmarkOutput.ParseReport("null"u8.ToArray()));
        Assert.NotNull(BenchmarkOutput.ParseReport(JsonSerializer.SerializeToUtf8Bytes(Pass(), BenchmarkHistoryStore.Json)));
    }

    [Fact]
    public void A_pass_file_over_one_megabyte_is_not_read()
    {
        var json = BenchmarkHistoryStore.Serialize(Pass());
        File.WriteAllText(Output(BenchmarkOutput.ReportFileName), json + new string(' ', (int)BenchmarkOutput.MaxReportBytes));

        Assert.Null(BenchmarkOutput.Read(_folder, 0).Report);
    }

    [Fact]
    public void An_image_without_the_png_signature_or_over_sixteen_megabytes_is_dropped_and_the_pass_kept()
    {
        BenchmarkOutput.WriteReport(_folder, Pass());
        File.WriteAllBytes(Output(BenchmarkOutput.ImageFileName), "GIF89a ce n'est pas un PNG"u8.ToArray());
        var notPng = BenchmarkOutput.Read(_folder, 0);
        var huge = new byte[BenchmarkOutput.MaxImageBytes + 1];
        Png.CopyTo(huge, 0);
        File.WriteAllBytes(Output(BenchmarkOutput.ImageFileName), huge);
        var tooBig = BenchmarkOutput.Read(_folder, 0);

        Assert.NotNull(notPng.Report);
        Assert.Null(notPng.Image);
        Assert.NotNull(tooBig.Report);
        Assert.Null(tooBig.Image);
    }

    [Theory]
    [InlineData(FileAttributes.Normal, 1u, 10L, true)]
    [InlineData(FileAttributes.Archive, 1u, 100L, true)]
    [InlineData(FileAttributes.ReparsePoint, 1u, 10L, false)]
    [InlineData(FileAttributes.Archive | FileAttributes.ReparsePoint, 1u, 10L, false)]
    [InlineData(FileAttributes.Directory, 1u, 10L, false)]
    [InlineData(FileAttributes.Normal, 2u, 10L, false)]
    [InlineData(FileAttributes.Normal, 0u, 10L, false)]
    [InlineData(FileAttributes.Normal, 1u, 101L, false)]
    public void Only_an_ordinary_file_is_read_never_a_link_or_a_second_name(FileAttributes attributes, uint links, long length, bool ordinary) =>
        Assert.Equal(ordinary, BenchmarkOutput.IsOrdinary(attributes, links, length, 100));

    [Fact]
    public void A_directory_in_place_of_the_pass_is_not_read()
    {
        Directory.CreateDirectory(Output(BenchmarkOutput.ReportFileName));

        Assert.Null(BenchmarkOutput.Read(_folder, 0).Report);
    }

    /// <summary>Le pire cas (passe de 1 Mo, image de 16 Mo) tient dans un message du canal, avec sa marge.</summary>
    [Fact]
    public void The_largest_answer_fits_in_one_engine_message()
    {
        // Une passe juste sous la borne de 1 Mo : des tests aux textes les plus longs que l'historique accepte (500 caractères).
        var test = new BenchmarkTestResult(new string('a', 500), "gpu", 120, new string('u', 500), 9000);
        var pass = Pass();
        var count = 0;
        for (var step = 64; step > 0; step /= 2)
        {
            while (BenchmarkHistoryStore.Serialize(pass with { Tests = [.. Enumerable.Repeat(test, count + step)] }).Length <= BenchmarkOutput.MaxReportBytes)
            {
                count += step;
            }
        }

        pass = pass with { Tests = [.. Enumerable.Repeat(test, count)] };

        var image = new byte[BenchmarkOutput.MaxImageBytes];
        Png.CopyTo(image, 0);
        var answer = JsonSerializer.SerializeToUtf8Bytes(new EngineResponse("result", EngineProtocol.ToElement(new BenchmarkOutcome(0, pass, image))), EngineProtocol.Json);

        Assert.True(answer.Length + 1 + EngineKey.MacSize < EngineProtocol.MaxMessageBytes, answer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---------- côté fenêtre : garder la passe rendue par le moteur

    [Fact]
    public void The_window_keeps_the_pass_and_writes_its_image_under_the_history_name()
    {
        var store = Store();
        var pass = Pass() with { Image = null };

        Assert.Null(BenchmarkOutput.Keep(new BenchmarkOutcome(0, pass, Png), store, elevated: false));

        var kept = Assert.Single(store.Load());
        Assert.Equal(store.ImagePathFor(pass.Date), kept.Image);
        Assert.Equal(Png, File.ReadAllBytes(kept.Image!));
    }

    [Fact]
    public void The_window_keeps_a_pass_whose_image_is_not_a_png_without_its_image()
    {
        var store = Store();

        Assert.Null(BenchmarkOutput.Keep(new BenchmarkOutcome(0, Pass(), "pas une image"u8.ToArray()), store, elevated: false));

        Assert.Null(Assert.Single(store.Load()).Image);
        Assert.False(Directory.Exists(store.ImagesFolder) && Directory.EnumerateFiles(store.ImagesFolder).Any());
    }

    [Fact]
    public void A_window_started_as_administrator_writes_nothing_in_the_profile_and_says_so()
    {
        var store = Store();

        var notKept = BenchmarkOutput.Keep(new BenchmarkOutcome(0, Pass(), Png), store, elevated: true);

        Assert.Contains("administrateur", notKept, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(store.FilePath)));
    }

    [Fact]
    public void Nothing_comes_back_when_the_benchmark_kept_its_pass_itself()
    {
        var store = Store();

        Assert.Null(BenchmarkOutput.Keep(new BenchmarkOutcome(0), store, elevated: false));

        Assert.False(Directory.Exists(Path.GetDirectoryName(store.FilePath)));
    }
}
