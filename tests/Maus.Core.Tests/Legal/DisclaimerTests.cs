using Maus.Core.Legal;
using Maus.Core.Preferences;

namespace Maus.Core.Tests.Legal;

public class DisclaimerTests
{
    private sealed class Protector : Maus.Core.Fixes.IDirectoryProtector
    {
        public void EnsureProtected(string directory) => Directory.CreateDirectory(directory);

        public bool IsTrusted(string file) => true;

        public void ProtectFile(string file)
        {
        }
    }

    [Fact]
    public void Warnings_say_no_warranty_user_responsibility_backup_and_consumer_rights()
    {
        var text = Disclaimer.Full;

        Assert.Contains("SANS AUCUNE GARANTIE", text, StringComparison.Ordinal);
        Assert.Contains("GPL-3.0", text, StringComparison.Ordinal);
        Assert.Contains("sous votre seule responsabilité", text, StringComparison.Ordinal);
        Assert.Contains("dans toute la mesure permise par la loi", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sauvegardez vos données", text, StringComparison.OrdinalIgnoreCase);
        // Une clause ne peut pas retirer ses droits à un consommateur : le texte doit le dire (fiche technique, mentions légales).
        Assert.Contains("ne limitent pas les droits", text, StringComparison.Ordinal);
        Assert.True(Disclaimer.Version >= 1);
    }

    [Fact]
    public void Every_reminder_repeats_no_warranty_and_responsibility()
    {
        foreach (var reminder in new[] { Disclaimer.TestReminder, Disclaimer.ChangeReminder, Disclaimer.OperationReminder })
        {
            Assert.Contains("sans garantie", reminder, StringComparison.Ordinal);
            Assert.Contains("sous votre responsabilité", reminder, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Accepted_version_is_kept_in_the_preferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maus-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FilePreferencesStore(directory, new Protector());
            Assert.Equal(0, store.Load().DisclaimerAccepted);

            store.Update(p => p with { DisclaimerAccepted = Disclaimer.Version });

            Assert.Equal(Disclaimer.Version, store.Load().DisclaimerAccepted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
