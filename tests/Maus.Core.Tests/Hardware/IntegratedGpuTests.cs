using Maus.Core.Hardware;

namespace Maus.Core.Tests.Hardware;

public sealed class IntegratedGpuTests
{
    [Theory]
    [InlineData(HardwareVendor.Intel, "Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData(HardwareVendor.Intel, "Intel(R) Arc(TM) Pro A60", false)]
    [InlineData(HardwareVendor.Intel, "Intel(R) Arc(TM) B580 Graphics", false)]
    [InlineData(HardwareVendor.Intel, "Intel(R) UHD Graphics 630", true)]
    [InlineData(HardwareVendor.Amd, "AMD Radeon RX 7800 XT", false)]
    [InlineData(HardwareVendor.Amd, "Radeon Pro WX 7100", false)]
    [InlineData(HardwareVendor.Amd, "Radeon RX Vega 56", false)]
    [InlineData(HardwareVendor.Amd, "Radeon RX Vega 11 Graphics", true)]
    [InlineData(HardwareVendor.Amd, "AMD Radeon(TM) Graphics", true)]
    public void A_dedicated_card_is_not_taken_for_an_integrated_one(HardwareVendor vendor, string name, bool integrated) =>
        Assert.Equal(integrated, GpuClassifier.IsLikelyIntegrated(vendor, name));
}
