using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public sealed class MissedAlertNoteTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("maus-alerte-").FullName;

    private string FilePath => Path.Combine(_folder, "sous-dossier", "note.txt");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_note_is_told_once()
    {
        var at = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(1));
        var note = new MissedAlertNote(FilePath);

        note.Write(at, 3);

        Assert.Equal((at, 3), note.Take());
        Assert.Null(note.Take());
    }

    [Fact]
    public void An_answered_alert_leaves_nothing()
    {
        var note = new MissedAlertNote(FilePath);
        note.Write(DateTimeOffset.Now, 1);

        note.Clear();

        Assert.Null(note.Take());
    }

    [Fact]
    public void No_note_is_not_an_error() =>
        Assert.Null(new MissedAlertNote(FilePath).Take());

    [Theory]
    [InlineData("")]
    [InlineData("abîmé")]
    [InlineData("2026-11-02T12:00:00+01:00;zéro")]
    [InlineData("2026-11-02T12:00:00+01:00;0")]
    [InlineData("2026-11-02T12:00:00+01:00;-1")]
    [InlineData("hier;3")]
    public void A_damaged_note_says_nothing_and_is_removed(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        Assert.Null(new MissedAlertNote(FilePath).Take());
        Assert.False(File.Exists(FilePath));
    }
}
