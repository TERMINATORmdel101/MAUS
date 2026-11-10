using System.IO;
using Maus.Core.Engine.Broker;
using Maus.Core.Workshop;

namespace Maus.App.Tests;

public class FollowPcFileTests
{
    private static InventoryReading Reading(string diskModel) => new(
        new HardwareInventory(
            new CpuIdentity("Processeur d'essai", "?", null, 4, 8, 0, null, null, null, [], false, false, false),
            new BoardIdentity(null, null, null, null, null),
            [],
            [],
            [new DiskIdentity(diskModel, "NVMe", "SSD", null, null, null, null, null)],
            []),
        null,
        null,
        false);

    [Fact]
    public void Nothing_is_read_before_the_button() => UiThread.Run(() =>
    {
        using var bench = new TestBench();

        Assert.False(bench.ViewModel.HasPcFilePreview);
        Assert.DoesNotContain("ReadInventoryAsync", bench.Fake.Calls);
        return Task.CompletedTask;
    });

    [Fact]
    public void The_user_sees_the_masked_content_then_the_page_goes_where_they_choose() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var folder = Directory.CreateTempSubdirectory("maus-pcfile-").FullName;
        try
        {
            var path = Path.Combine(folder, "dossier.html");
            var name = Environment.UserName;
            bench.Fake.Answers["ReadInventoryAsync"] = _ => Reading($"Disque de {name} S/N: SECRET42");
            bench.ViewModel.PickPcFilePath = _ => path;

            bench.ViewModel.SavePcFileCommand.Execute(null);
            Assert.False(File.Exists(path));

            await bench.ViewModel.PreparePcFileAsyncForTest();

            Assert.True(bench.ViewModel.HasPcFilePreview);
            Assert.Contains("Relevé par MAUS, non certifié", bench.ViewModel.PcFilePreview, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET42", bench.ViewModel.PcFilePreview, StringComparison.Ordinal);
            Assert.False(File.Exists(path));

            await bench.ViewModel.SavePcFileAsyncForTest();

            var html = await File.ReadAllTextAsync(path);
            Assert.Contains("Relevé par MAUS, non certifié", html, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET42", html, StringComparison.Ordinal);
            Assert.DoesNotContain($"Disque de {name}", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void Giving_up_writes_nothing() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["ReadInventoryAsync"] = _ => Reading("Disque");
        bench.ViewModel.PickPcFilePath = _ => null;
        await bench.ViewModel.PreparePcFileAsyncForTest();
        var status = bench.ViewModel.PcFileStatus;

        await bench.ViewModel.SavePcFileAsyncForTest();

        Assert.Equal(status, bench.ViewModel.PcFileStatus);
    });

    [Fact]
    public void A_failed_reading_says_so_and_leaves_nothing_to_write() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["ReadInventoryAsync"] = _ => new IOException("essai");

        await bench.ViewModel.PreparePcFileAsyncForTest();

        Assert.False(bench.ViewModel.HasPcFilePreview);
        Assert.Contains("a échoué", bench.ViewModel.PcFileStatus, StringComparison.Ordinal);
        Assert.False(bench.ViewModel.IsPreparingPcFile);
    });

    [Fact]
    public void An_unwritable_path_is_reported() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        var folder = Directory.CreateTempSubdirectory("maus-pcfile-").FullName;
        try
        {
            bench.Fake.Answers["ReadInventoryAsync"] = _ => Reading("Disque");
            bench.ViewModel.PickPcFilePath = _ => folder;
            await bench.ViewModel.PreparePcFileAsyncForTest();

            await bench.ViewModel.SavePcFileAsyncForTest();

            Assert.Contains("n'a pas pu être enregistré", bench.ViewModel.PcFileStatus, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void Printing_sends_the_text_the_user_read() => UiThread.Run(async () =>
    {
        using var bench = new TestBench();
        bench.Fake.Answers["ReadInventoryAsync"] = _ => Reading("Disque");
        string? printed = null;
        bench.ViewModel.PrintPcFileText = text =>
        {
            printed = text;
            return true;
        };
        await bench.ViewModel.PreparePcFileAsyncForTest();

        bench.ViewModel.PrintPcFileCommand.Execute(null);

        Assert.Equal(bench.ViewModel.PcFilePreview, printed);
    });
}
