using Maus.Core.Hardware;

namespace Maus.Core.Tests.Hardware;

public class ClassifierTests
{
    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", true)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", true)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", false)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", false)]
    [InlineData("AMD Ryzen 9 9950X3D2 16-Core Processor", false)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", false)]
    public void Detects_asymmetric_dual_ccd_x3d(string name, bool expected) =>
        Assert.Equal(expected, CpuClassifier.IsAsymmetricDualCcdX3D(name));

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", true)]
    [InlineData("AMD Ryzen 9 9950X3D2 16-Core Processor", true)]
    [InlineData("AMD Ryzen 7 7700X 8-Core Processor", false)]
    public void Detects_any_x3d(string name, bool expected) => Assert.Equal(expected, CpuClassifier.IsX3D(name));

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", true)]
    [InlineData("14th Gen Intel(R) Core(TM) i7-14700KF", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-13400F", true)]
    [InlineData("14th Gen Intel(R) Core(TM) i9-14900HX", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-1355U", false)]
    [InlineData("12th Gen Intel(R) Core(TM) i9-12900K", false)]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", false)]
    public void Detects_raptor_lake_desktop(string name, bool expected) =>
        Assert.Equal(expected, CpuClassifier.IsIntelRaptorLakeDesktop(name));

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_1E07", "NVIDIA GeForce RTX 2080 Ti", HardwareVendor.Nvidia, false)]
    [InlineData(@"PCI\VEN_1002&DEV_744C", "AMD Radeon RX 7900 XTX", HardwareVendor.Amd, false)]
    [InlineData(@"PCI\VEN_1002&DEV_164E", "AMD Radeon(TM) Graphics", HardwareVendor.Amd, true)]
    [InlineData(@"PCI\VEN_1002&DEV_15BF", "AMD Radeon 780M Graphics", HardwareVendor.Amd, true)]
    [InlineData(@"PCI\VEN_8086&DEV_56A0", "Intel(R) Arc(TM) A770 Graphics", HardwareVendor.Intel, false)]
    [InlineData(@"PCI\VEN_8086&DEV_E20B", "Intel(R) Arc(TM) B580 Graphics", HardwareVendor.Intel, false)]
    [InlineData(@"PCI\VEN_8086&DEV_64A0", "Intel(R) Arc(TM) 140V GPU", HardwareVendor.Intel, true)]
    [InlineData(@"PCI\VEN_8086&DEV_3E92", "Intel(R) UHD Graphics 630", HardwareVendor.Intel, true)]
    public void Classifies_gpus(string pnp, string name, HardwareVendor vendor, bool integrated)
    {
        var detected = GpuClassifier.VendorOf(pnp, name);

        Assert.Equal(vendor, detected);
        Assert.Equal(integrated, GpuClassifier.IsLikelyIntegrated(detected, name));
    }

    [Fact]
    public void Laptop_needs_two_of_three_signals()
    {
        Assert.Equal(FormFactor.Laptop, FormFactorClassifier.Classify([10], 2, hasBattery: false));
        Assert.Equal(FormFactor.Laptop, FormFactorClassifier.Classify([3], 2, hasBattery: true));
        Assert.Equal(FormFactor.Desktop, FormFactorClassifier.Classify([3], 1, hasBattery: true)); // onduleur USB
        Assert.Equal(FormFactor.Desktop, FormFactorClassifier.Classify([10], 1, hasBattery: false));
        Assert.Equal(FormFactor.Unknown, FormFactorClassifier.Classify([], null, hasBattery: false));
    }
}
