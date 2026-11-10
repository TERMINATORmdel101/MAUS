using Maus.Core.Reporting;

namespace Maus.Core.Tests.Reporting;

public class MaskingOrderTests
{
    private static readonly PrivacyFilter Filter = new("alex", "PC-1", @"C:\Users\alex");

    [Theory]
    [InlineData(@"C:\Users\alex\Docs", @"%USERPROFILE%\Docs")]
    [InlineData(@"C:\Users\alexandra\Docs", @"%SystemDrive%\Users\[compte]\Docs")]
    [InlineData(@"D:/Users/Marie/Docs", @"%SystemDrive%\Users\[compte]/Docs")]
    public void Every_account_folder_is_masked(string text, string expected) =>
        Assert.Equal(expected, Filter.Mask(text));

    [Theory]
    [InlineData("camille", null, null, "camille@corp.example écrit", "[e-mail] écrit")]
    [InlineData("camille", null, null, "camille2024.docx", "[utilisateur]2024.docx")]
    [InlineData("camille", null, null, "camillePC", "[utilisateur]PC")]
    [InlineData("alex", null, null, "alex2024.docx", "[utilisateur]2024.docx")]
    [InlineData(null, "DESKTOP-4KQ7ZPL", null, "DESKTOP-4KQ7ZPLX", "[nom-du-pc]X")]
    [InlineData("camille", null, "CORP", @"CORP\camille", @"[domaine]\[utilisateur]")]
    [InlineData("camille", null, "CORP", "domaine CORP", "domaine [domaine]")]
    [InlineData("camille", null, null, @"AzureAD\camille", @"[domaine]\[utilisateur]")]
    [InlineData("camille", null, null, @"D:\Jeux\camille\saves", @"D:\Jeux\[utilisateur]\saves")]
    [InlineData("Jo", null, null, @"D:\Jeux\Jo\saves", @"D:\Jeux\[utilisateur]\saves")]
    [InlineData("Jo", null, null, @"CORP\Jo", @"[domaine]\[utilisateur]")]
    [InlineData("Jo", null, null, @"D:\Jeux\Jours\saves", @"D:\Jeux\Jours\saves")]
    [InlineData("Jo", null, null, "Joie de Jo", "Joie de Jo")]
    [InlineData(null, null, null, "OneDrive Update Task-S-1-5-21-111-222-333-1001", "OneDrive Update Task-[SID]")]
    [InlineData(null, null, null, "GoogleUpdateTaskUserS-1-5-21-111-222-333-1001Core", "GoogleUpdateTaskUser[SID]Core")]
    [InlineData(null, null, null, "compte S-1-12-1-111-222-333-444", "compte [SID]")]
    [InlineData(null, null, null, "service S-1-5-18", "service S-1-5-18")]
    public void What_identifies_the_person_is_masked_in_every_form(string? user, string? machine, string? domain, string text, string expected) =>
        Assert.Equal(expected, new PrivacyFilter(user, machine, null, domain).Mask(text));

    [Fact]
    public void A_local_account_does_not_mask_its_machine_name_as_a_domain()
    {
        var filter = new PrivacyFilter("camille", "PC-BUREAU", null, "PC-BUREAU");

        Assert.Equal("[nom-du-pc]", filter.Mask("PC-BUREAU"));
        Assert.DoesNotContain(Maus.Core.Localization.Texts.T("domaine Windows"), filter.Removed);
    }

    [Fact]
    public void The_note_says_what_is_not_recognised()
    {
        var note = HelpSummary.AnonymizationNote(new PrivacyFilter(null, null, null));

        Assert.Contains(Maus.Core.Localization.Texts.T("MAUS ne repère pas un nom complet ni une adresse IP, et ne les masque donc pas : relisez le texte avant de le partager."), note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mention_or_a_ticket_number_does_not_become_a_link_in_the_copied_text()
    {
        Assert.Equal(@"- jeu \@pseudo \#12", HelpSummary.ToMarkdown("- jeu @pseudo #12"));
    }

    [Fact]
    public void A_field_of_the_json_export_is_masked_value_by_value()
    {
        const string value = @"C:\Users\camille.dupont\Downloads";
        var masked = new PrivacyFilter("alex", null, @"C:\Users\alex").Mask(value);

        Assert.DoesNotContain("dupont", masked, StringComparison.Ordinal);
    }
}
