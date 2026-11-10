using Maus.Core.Platform;

namespace Maus.Core.Tests.Platform;

/// <summary>Activation de privilèges (0.8.6) : jamais d'exception, et rien n'est activé sans droits administrateur.</summary>
public class TokenPrivilegesTests
{
    [Fact]
    public void Enabling_never_throws_and_reports_what_it_enabled()
    {
        using var scope = TokenPrivileges.Enable(TokenPrivileges.TakeOwnership, TokenPrivileges.Restore, "SeDoesNotExistPrivilege");

        Assert.DoesNotContain("SeDoesNotExistPrivilege", scope.Enabled);

        // La reprise d'un dossier hostile compte dessus : avec les droits administrateur ils sont activés, sans ces droits aucun.
        if (Environment.IsPrivilegedProcess)
        {
            Assert.Contains(TokenPrivileges.TakeOwnership, scope.Enabled);
            Assert.Contains(TokenPrivileges.Restore, scope.Enabled);
        }
        else
        {
            Assert.Empty(scope.Enabled);
        }
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var scope = TokenPrivileges.Enable();
        scope.Dispose();
        scope.Dispose();
        Assert.Empty(scope.Enabled);
    }
}
