using Maus.Core.Engine.Broker;

namespace Maus.Core.Tests.Engine;

/// <summary>Le moteur administrateur valide sa ligne de commande avant tout : seule la forme exacte passe.</summary>
public sealed class EngineCommandLineTests
{
    private const string Pipe = "MAUS-0123456789abcdef0123456789abcdef";
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Exact_form_is_accepted()
    {
        Assert.True(EngineCommandLine.TryParse(Pipe, "4242", Key, out var pid));
        Assert.Equal(4242, pid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MAUS-")]
    [InlineData("MAUS-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("MAUS-0123456789abcdef0123456789abcde")]
    [InlineData("MAUS-0123456789abcdef0123456789abcdef0")]
    [InlineData("AUTRE-0123456789abcdef0123456789abcdef")]
    [InlineData("MAUS-0123456789abcdef0123456789abcdef\n")]
    [InlineData(@"\\.\pipe\MAUS-0123456789abcdef0123456789abcdef")]
    public void Any_other_pipe_name_is_refused(string pipe) =>
        Assert.False(EngineCommandLine.TryParse(pipe, "4242", Key, out _));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("0x10")]
    [InlineData("1,5")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public void Any_other_process_number_is_refused(string pid) =>
        Assert.False(EngineCommandLine.TryParse(Pipe, pid, Key, out _));

    [Theory]
    [InlineData("")]
    [InlineData("0123")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void Any_other_fingerprint_is_refused(string key) =>
        Assert.False(EngineCommandLine.TryParse(Pipe, "4242", key, out _));
}
