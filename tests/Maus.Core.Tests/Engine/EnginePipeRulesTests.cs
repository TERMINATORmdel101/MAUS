using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Maus.Core.Engine.Broker;

namespace Maus.Core.Tests.Engine;

public class EnginePipeRulesTests
{
    private static readonly SecurityIdentifier Engine = new("S-1-5-21-1-2-3-500");

    private static List<PipeAccessRule> Rules(string? session) =>
        EngineServer.PipeRules(Engine, session).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

    [Fact]
    public void Same_account_gets_no_extra_rule()
    {
        Assert.Equal(2, Rules(Engine.Value).Count);
        Assert.Equal(2, Rules(null).Count);
        Assert.Contains(Rules(null), r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Value == "S-1-5-2");
    }

    [Fact]
    public void The_person_at_the_screen_only_reads_and_writes()
    {
        const string person = "S-1-5-21-1-2-3-1001";

        var rule = Rules(person).Single(r => r.IdentityReference.Value == person);

        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.ReadWrite, rule.PipeAccessRights & ~PipeAccessRights.Synchronize);
        Assert.Equal(0, (int)(rule.PipeAccessRights & PipeAccessRights.CreateNewInstance));
    }
}
