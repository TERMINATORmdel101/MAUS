using Maus.Core.Fixes;
using Microsoft.Win32;

namespace Maus.Core.Tests.Fixes;

public class SettingValueTests
{
    [Fact]
    public void Integers_compare_numerically_across_dword_and_qword()
    {
        Assert.True(SettingValue.AreEquivalent(SettingValue.Dword(1), new SettingValue(RegistryValueKind.QWord, "1")));
        Assert.False(SettingValue.AreEquivalent(SettingValue.Dword(1), SettingValue.Text("1")));
        Assert.True(SettingValue.AreEquivalent(null, null));
        Assert.False(SettingValue.AreEquivalent(null, SettingValue.Dword(0)));
    }

    [Fact]
    public void Registry_values_round_trip_with_their_type()
    {
        Assert.Equal(-1, SettingValue.FromRegistry(-1, RegistryValueKind.DWord)!.ToRegistryObject());
        Assert.Equal(new byte[] { 1, 2 }, SettingValue.FromRegistry(new byte[] { 1, 2 }, null)!.ToRegistryObject());
        Assert.Equal(RegistryValueKind.ExpandString, SettingValue.FromRegistry("%X%", RegistryValueKind.ExpandString)!.Kind);
        Assert.Null(SettingValue.FromRegistry(null, null));
    }

    [Fact]
    public void A_none_value_keeps_its_type()
    {
        var value = SettingValue.FromRegistry(new byte[] { 1 }, RegistryValueKind.None)!;

        Assert.Equal(RegistryValueKind.None, value.Kind);
        Assert.Equal(new byte[] { 1 }, value.ToRegistryObject());
    }

    [Fact]
    public void A_dword_journaled_as_unsigned_is_the_same_value()
    {
        // Le registre rend -1 ; une séance peut avoir noté 4294967295 pour la même valeur.
        Assert.True(SettingValue.AreEquivalent(new SettingValue(RegistryValueKind.DWord, "4294967295"), SettingValue.Dword(-1)));
        Assert.Equal(-1, new SettingValue(RegistryValueKind.DWord, "4294967295").ToRegistryObject());
        Assert.False(SettingValue.AreEquivalent(new SettingValue(RegistryValueKind.DWord, "4294967294"), SettingValue.Dword(-1)));
        Assert.False(SettingValue.AreEquivalent(new SettingValue(RegistryValueKind.QWord, "4294967295"), SettingValue.Dword(-1)));
    }

    [Fact]
    public void An_empty_list_and_a_bad_number_are_handled()
    {
        Assert.Empty((string[])new SettingValue(RegistryValueKind.MultiString, string.Empty).ToRegistryObject());
        Assert.Throws<FormatException>(() => new SettingValue(RegistryValueKind.DWord, "abc").AsBool());
    }
}
