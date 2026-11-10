using Maus.Core.Workshop.Memory;

namespace Maus.Core.Tests.Workshop;

/// <summary>Garde du bloc EXPO (0.8.6) : la somme de contrôle est aux octets 958 et 959, une image plus courte ne doit pas être lue.</summary>
public class SpdExpoGuardTests
{
    [Theory]
    [InlineData(1024, 1)]
    [InlineData(960, 1)]
    [InlineData(959, 0)]
    [InlineData(922, 0)]
    public void Expo_profile_needs_the_whole_block(int length, int expected)
    {
        var bytes = MemoryDetailsTests.Ddr5Kit()[..length];

        var module = SpdDecoder.Decode(new SpdImage(0, bytes));

        Assert.NotNull(module);
        Assert.Equal(expected, module.Profiles.Count(p => p.Kind == ProfileKind.Expo));
    }
}
