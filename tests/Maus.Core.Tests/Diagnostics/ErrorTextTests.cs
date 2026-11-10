using System.ComponentModel;
using Maus.Core.Diagnostics;
using Maus.Core.Engine.Broker;
using Maus.Core.Reporting;

namespace Maus.Core.Tests.Diagnostics;

/// <summary>Messages d'erreur humains (0.8.6) : une phrase et « que faire », les exceptions de MAUS inchangées, détails en mode technicien.</summary>
public class ErrorTextTests
{
    [Fact]
    public void Windows_errors_become_a_sentence_with_what_to_do()
    {
        ErrorText.Verbose = false;
        var text = ErrorText.For(new UnauthorizedAccessException("Access to the path 'C:\\x' is denied."));

        Assert.DoesNotContain("Access to the path", text, StringComparison.Ordinal);
        Assert.Contains("Que faire", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declined_uac_prompt_is_not_an_error() =>
        Assert.Contains("annulé", ErrorText.For(new Win32Exception(1223)), StringComparison.Ordinal);

    [Fact]
    public void Maus_own_exceptions_keep_their_message()
    {
        ErrorText.Verbose = true;
        try
        {
            Assert.Equal("Le moteur est arrêté.", ErrorText.For(new EngineException("Le moteur est arrêté.")));
        }
        finally
        {
            ErrorText.Verbose = false;
        }
    }

    [Fact]
    public void Technician_mode_appends_the_original_message()
    {
        ErrorText.Verbose = true;
        try
        {
            var text = ErrorText.For(new IOException("The process cannot access the file\nsecond line"));
            Assert.Contains("[IOException : The process cannot access the file]", text, StringComparison.Ordinal);
        }
        finally
        {
            ErrorText.Verbose = false;
        }
    }

    [Fact]
    public void Confirmation_text_follows_one_template()
    {
        var text = ConfirmationText.Build("Ce qui va changer.", risk: "un risque", undo: "comment revenir", reminder: "Rappel.");

        Assert.Equal(["Ce qui va changer.", "À savoir : un risque", "Retour en arrière : comment revenir", "Rappel.", "Continuer ?"], text.Split(Environment.NewLine + Environment.NewLine));
        Assert.Equal("Seulement ceci." + Environment.NewLine + Environment.NewLine + "Continuer ?", ConfirmationText.Build("Seulement ceci. "));
    }
}
