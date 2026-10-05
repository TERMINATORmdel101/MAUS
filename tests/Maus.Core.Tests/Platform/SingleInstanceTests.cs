using Maus.Core.Platform;

namespace Maus.Core.Tests.Platform;

/// <summary>
/// « Un seul MAUS à la fois » ne concerne que l'interface (MAUS.exe) : Windows renvoie aussi l'outil en ligne de commande
/// (maus.exe) quand on cherche « MAUS », et une correction peut y être en cours.
/// </summary>
public class SingleInstanceTests
{
    private const int Current = 100;

    [Fact]
    public void Another_interface_is_found()
    {
        Assert.True(SingleInstance.IsOtherInterface(200, "MAUS", Current, "MAUS"));
    }

    [Fact]
    public void The_current_process_is_never_an_other()
    {
        Assert.False(SingleInstance.IsOtherInterface(Current, "MAUS", Current, "MAUS"));
    }

    [Theory]
    [InlineData("maus")]
    [InlineData("Maus")]
    [InlineData("mAUS")]
    public void The_command_line_tool_is_never_taken_for_the_interface(string name)
    {
        // Ni ramené devant, ni fermé comme « MAUS sans fenêtre » : un « maus --apply-recommended » peut y être en cours.
        Assert.False(SingleInstance.IsOtherInterface(200, name, Current, "MAUS"));
    }

    [Fact]
    public void An_unrelated_program_is_ignored()
    {
        Assert.False(SingleInstance.IsOtherInterface(200, "MAUS2", Current, "MAUS"));
    }
}
