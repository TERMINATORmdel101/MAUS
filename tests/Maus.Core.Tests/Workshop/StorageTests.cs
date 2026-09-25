using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maus-space-" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string relative, long bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(bytes);
        return path;
    }

    [Fact]
    public void Sizes_add_up_and_big_files_stand_out()
    {
        Make(Path.Combine("videos", "film.mkv"), 3 << 20);
        Make(Path.Combine("videos", "extras", "bonus.mkv"), 2 << 20);
        for (var i = 0; i < 5; i++)
        {
            Make(Path.Combine("documents", $"note{i}.txt"), 1000);
        }

        var report = SpaceAnalyzer.Scan(_root);

        Assert.Equal((3L << 20) + (2L << 20) + 5000, report.Root.Bytes);
        Assert.Equal(7, report.Files);
        Assert.Equal("videos", report.Root.Folders[0].Name);
        Assert.Equal("film.mkv", report.Largest[0].Name);
        Assert.Equal("bonus.mkv", report.Largest[1].Name);
        var documents = report.Root.Folders.Single(f => f.Name == "documents");
        Assert.Empty(documents.BigFiles);
        Assert.Equal(5, documents.OtherFiles);
        Assert.Equal(5000, documents.OtherBytes);
        Assert.Same(report.Root, documents.Parent);
    }

    [Fact]
    public void Scanning_can_be_stopped()
    {
        Make("a.bin", 10);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SpaceAnalyzer.Scan(_root, cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(@"C:\hiberfil.sys", "Veille prolongée")]
    [InlineData(@"C:\Windows\WinSxS", "Magasin des composants")]
    [InlineData(@"C:\Users\Alex\AppData\Local\Temp", "temporaires")]
    [InlineData(@"C:\Windows.old", "Ancienne version")]
    public void Well_known_space_eaters_are_explained(string path, string expected)
    {
        Assert.Contains(expected, SpaceHints.Describe(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_folders_get_no_hint()
    {
        Assert.Null(SpaceHints.Describe(@"C:\Users\Alex\Musique"));
    }

    [Fact]
    public async Task Disk_speed_test_measures_and_cleans_up()
    {
        var result = await DiskSpeedTest.RunAsync(_root, 8L << 20, TimeSpan.FromMilliseconds(200), null, CancellationToken.None);

        Assert.False(result.Aborted);
        Assert.True(result.WriteMegabytesPerSecond > 0);
        Assert.True(result.ReadMegabytesPerSecond > 0);
        Assert.True(result.RandomReadsPerSecond > 0);
        Assert.Empty(Directory.GetFiles(_root, "MAUS-test-*"));
    }
}
