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

        // Le profil d'un autre compte n'est jamais lu : compté comme illisible, ses fichiers n'apparaissent nulle part.
        var skipped = SpaceAnalyzer.Scan(_root, skip: [Path.Combine(_root, "videos")]);
        Assert.Equal(5000, skipped.Root.Bytes);
        Assert.Equal(1, skipped.Inaccessible);
        Assert.DoesNotContain(skipped.Largest, f => f.Name.EndsWith(".mkv", StringComparison.Ordinal));
        Assert.True(SpaceAnalyzer.IsUnder(Path.Combine(_root, "videos", "extras"), [Path.Combine(_root, "videos")]));
        Assert.False(SpaceAnalyzer.IsUnder(Path.Combine(_root, "videos2"), [Path.Combine(_root, "videos")]));

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
    public void Very_deep_folders_stop_at_the_depth_limit_instead_of_crashing()
    {
        var deep = _root;
        for (var i = 0; i < SpaceAnalyzer.MaxDepth + 20; i++)
        {
            deep = Path.Combine(deep, "d");
        }

        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "fond.bin"), new byte[10]);

        var report = SpaceAnalyzer.Scan(_root);

        Assert.Equal(1, report.Inaccessible);
        Assert.Equal(0, report.Files);
    }

    [Theory]
    [InlineData(@"C:\Windows", true)]
    [InlineData("C:/Windows", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"\\serveur\partage", false)]
    [InlineData("//serveur/partage", false)]
    [InlineData(@"\/serveur/partage", false)]
    [InlineData(@"/\serveur\partage", false)]
    [InlineData(@"\\?\C:\Windows", false)]
    [InlineData("//?/C:/Windows", false)]
    [InlineData(@"\\.\C:\Windows", false)]
    [InlineData("//./C:/Windows", false)]
    [InlineData(@"\??\C:\Windows", false)]
    [InlineData(@"\Windows", false)]
    [InlineData("C:Windows", false)]
    [InlineData("Windows", false)]
    public void Only_a_local_folder_can_be_scanned(string root, bool accepted) =>
        Assert.Equal(accepted, SpaceAnalyzer.IsLocalRoot(root));

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
    public void A_very_deep_tree_goes_through_the_engine_channel()
    {
        var root = new SpaceNode { Path = @"C:\", Name = "C:", Bytes = 8L << 30, Files = 80 };
        var parent = root;
        for (var i = 0; i < 80; i++)
        {
            var child = new SpaceNode { Path = parent.Path + @"\d", Name = "d", Bytes = 8L << 30, Files = 80 };
            parent.Folders.Add(child);
            parent = child;
        }

        SpaceAnalyzer.Compact(root, SpaceAnalyzer.MaxChannelEntries);

        var element = Maus.Core.Engine.Broker.EngineProtocol.ToElement(new SpaceReport(root, [], 80, 81, 0, TimeSpan.Zero));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, element.ValueKind);
    }

    [Fact]
    public void Recycle_bins_of_other_accounts_and_old_profiles_are_set_apart()
    {
        Directory.CreateDirectory(Path.Combine(_root, "$Recycle.Bin", "S-1-5-21-1"));
        Directory.CreateDirectory(Path.Combine(_root, "$Recycle.Bin", "S-1-5-21-2"));

        var folders = SpaceAnalyzer.OtherAccountFolders(_root, "S-1-5-21-1");

        Assert.Contains(Path.Combine(_root, "Windows.old", "Users"), folders);
        Assert.Contains(Path.Combine(_root, "$Recycle.Bin", "S-1-5-21-2"), folders);
        Assert.DoesNotContain(Path.Combine(_root, "$Recycle.Bin", "S-1-5-21-1"), folders);
    }

    [Fact]
    public void A_whole_drive_fits_in_one_message_of_the_engine_and_keeps_its_sizes()
    {
        var root = new SpaceNode { Path = @"C:\", Name = "C:" };
        for (var i = 0; i < 200_000; i++)
        {
            var folder = new SpaceNode { Path = $@"C:\Dossier\Sous-dossier éloigné\{i:D6}", Name = $"{i:D6}", Bytes = 1 + (i * 200L), Files = 2 };
            if (i % 4 == 0)
            {
                folder.BigFiles.Add(new SpaceFile(folder.Path + @"\gros.bin", "gros.bin", folder.Bytes));
            }

            root.Folders.Add(folder);
            root.Bytes += folder.Bytes;
            root.Files += folder.Files;
        }

        root.Folders.Add(new SpaceNode { Path = @"C:\Illisible", Name = "Illisible", Inaccessible = true });
        var bytes = root.Bytes;
        SpaceAnalyzer.Compact(root, SpaceAnalyzer.MaxChannelEntries);

        Assert.True(root.Folders.Count + root.Folders.Sum(f => f.BigFiles.Count) <= SpaceAnalyzer.MaxChannelEntries);
        Assert.Contains(root.Folders, f => f.Inaccessible);
        Assert.Equal(bytes, root.OtherBytes + root.Folders.Sum(f => f.Bytes));
        var report = new SpaceReport(root, [], root.Files, 200_001, 1, TimeSpan.FromMinutes(1));
        var message = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Maus.Core.Engine.Broker.EngineResponse("result", Maus.Core.Engine.Broker.EngineProtocol.ToElement(report)), Maus.Core.Engine.Broker.EngineProtocol.Json);
        Assert.True(message.Length < Maus.Core.Engine.Broker.EngineProtocol.MaxMessageBytes, $"{message.Length} octets");
    }

    [Fact]
    public void Disk_speed_test_positions_go_past_two_gigabytes() =>
        Assert.Equal(1L << 31, DiskSpeedTest.BlockOffset(2048));

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

    [Fact]
    public async Task Disk_test_stopped_before_the_start_is_interrupted_not_an_exception()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var result = await DiskSpeedTest.RunAsync(_root, 8L << 20, TimeSpan.FromMilliseconds(200), null, stop.Token);

        Assert.True(result.Aborted);
    }
}
