using Maus.Core.Platform;

namespace Maus.Core.Tests.Platform;

public class ProcessHardeningTests
{
    [Fact]
    public void Protections_are_applied_on_windows_and_never_throw_elsewhere()
    {
        // Seule la protection de la fenêtre est essayée ici : celles du moteur changeraient le processus des tests.
        var applied = ProcessHardening.HardenInterface();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(["images"], applied);
        }
        else
        {
            Assert.Empty(applied);
        }
    }
}
