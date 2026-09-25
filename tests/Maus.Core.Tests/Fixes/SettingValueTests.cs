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
}
