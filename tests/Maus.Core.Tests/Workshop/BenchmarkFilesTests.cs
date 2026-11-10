using Maus.Bench;
using Maus.Bench.Gpu;
using Maus.Core.Workshop.Benchmark;

namespace Maus.Core.Tests.Workshop;

/// <summary>Fichiers du benchmark : cache des shaders, vignettes temporaires, options de la ligne de commande.</summary>
public sealed class BenchmarkFilesTests
{
    private static byte[] Container(int declaredLength, int actualLength, string magic = "DXBC")
    {
        var bytes = new byte[actualLength];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), declaredLength);
        return bytes;
    }

    [Fact]
    public void A_complete_shader_is_kept_and_a_truncated_or_foreign_file_is_compiled_again()
    {
        Assert.True(ShaderLibrary.IsCompleteBytecode(Container(64, 64)));
        Assert.False(ShaderLibrary.IsCompleteBytecode(Container(64, 40)));
        Assert.False(ShaderLibrary.IsCompleteBytecode(Container(64, 64, "XXXX")));
        Assert.False(ShaderLibrary.IsCompleteBytecode([]));
    }

    [Fact]
    public void Thumbnails_live_in_their_own_folder_which_disappears_with_the_store()
    {
        string folder;
        using (var store = new SharePictureStore())
        {
            Assert.Null(store.Folder);
            var picture = store.Save("ring", new byte[480 * 270 * 4], 480, 270);
            Assert.NotNull(picture);
            folder = store.Folder!;
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), folder, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(picture.File));
        }

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Two_stores_never_share_a_folder()
    {
        using var first = new SharePictureStore();
        using var second = new SharePictureStore();
        first.Save("ring", new byte[480 * 270 * 4], 480, 270);
        second.Save("ring", new byte[480 * 270 * 4], 480, 270);

        Assert.NotEqual(first.Folder, second.Folder);
    }

    [Fact]
    public void A_file_left_by_someone_else_is_not_deleted()
    {
        string other;
        string folder;
        using (var store = new SharePictureStore())
        {
            store.Save("ring", new byte[480 * 270 * 4], 480, 270);
            folder = store.Folder!;
            other = Path.Combine(folder, "autre.txt");
            File.WriteAllText(other, "x");
        }

        try
        {
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_unit_shown_comes_from_the_test_not_from_the_language_of_the_day_it_was_saved()
    {
        var saved = new BenchmarkTestResult("cpu-vector", "cpu", 4.4, "texte d'une autre langue", 10000);
        var shared = new BenchmarkTestResult("cpu-render", "cpu", 26, "", 10000);
        var future = new BenchmarkTestResult("cpu-futur", "cpu", 1, "unité écrite", 10000);

        Assert.Contains(BenchmarkTexts.Unit("cpu-vector"), BenchmarkTexts.Measure(saved), StringComparison.Ordinal);
        Assert.DoesNotContain("autre langue", BenchmarkTexts.Measure(saved), StringComparison.Ordinal);
        Assert.Contains(BenchmarkTexts.Unit("cpu-render"), BenchmarkTexts.Measure(shared), StringComparison.Ordinal);
        Assert.Contains("unité écrite", BenchmarkTexts.Measure(future), StringComparison.Ordinal);
    }

    [Fact]
    public void A_history_with_the_old_capability_field_is_still_read()
    {
        const string old = "{\"date\":\"2026-10-01T00:00:00+00:00\",\"tests\":[{\"id\":\"ring\",\"device\":\"gpu\",\"capability\":\"geometry\",\"value\":30,\"unit\":\"images par seconde\",\"score\":9000}]}";

        var report = System.Text.Json.JsonSerializer.Deserialize<BenchmarkReport>(old, BenchmarkHistoryStore.Json);

        Assert.Equal(9000, report!.Tests[0].Score);
    }

    [Theory]
    [InlineData("--duration-scale", "abc")]
    [InlineData("--times", "1,x")]
    [InlineData("--size", "ax10")]
    [InlineData("--camera", "1,2")]
    public void A_badly_written_option_is_refused_with_a_message_instead_of_a_crash(string option, string value)
    {
        Assert.False(BenchOptions.TryParse([option, value], out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Correct_options_are_read()
    {
        Assert.True(BenchOptions.TryParse(["--resolution", "4k", "--duration-scale", "0.5"], out var options, out _));
        Assert.Equal(2160, options!.RenderSize.Height);
        Assert.True(options.IsTrial);
    }
}
