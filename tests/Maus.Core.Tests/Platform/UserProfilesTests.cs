using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Platform;

/// <summary>Profils des autres comptes (décision 10) et chemin réel d'un dossier.</summary>
public class UserProfilesTests
{
    private const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    [Fact]
    public void Every_profile_but_mine_is_listed()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, Key + @"\S-1-5-21-1-1001", "ProfileImagePath", @"C:\Users\Moi")
            .Set(RegistryHive.LocalMachine, Key + @"\S-1-5-21-1-1001.bak", "ProfileImagePath", @"C:\Users\Moi")
            .Set(RegistryHive.LocalMachine, Key + @"\S-1-5-21-1-1002", "ProfileImagePath", @"D:\Comptes\Autre\");

        Assert.Equal([@"D:\Comptes\Autre"], UserProfiles.OfOthers(registry, "S-1-5-21-1-1001"));
    }

    [Fact]
    public void The_final_path_of_a_folder_is_read_from_windows()
    {
        var temp = Path.GetTempPath();

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(new DirectoryInfo(FinalPath.Of(temp)!).FullName)), Path.TrimEndingDirectorySeparator(FinalPath.Of(temp)!));
        Assert.Null(FinalPath.Of(Path.Combine(temp, Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void A_network_path_is_told_from_a_local_one()
    {
        Assert.True(FinalPath.IsOnNetworkDrive(@"\\serveur\partage\MAUS.exe"));
        Assert.True(FinalPath.IsOnNetworkDrive(@"//serveur/partage/MAUS.exe"));
        Assert.False(FinalPath.IsOnNetworkDrive(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        Assert.False(FinalPath.IsOnNetworkDrive("relatif.exe"));
    }

    [Fact]
    public void The_final_path_of_a_junction_is_its_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "maus-tests-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "reel");
        var link = Path.Combine(root, "lien");
        Directory.CreateDirectory(target);
        try
        {
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            {
                mklink.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }

            Assert.EndsWith(@"\reel", FinalPath.Of(link), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Profiles_are_also_compared_by_their_real_path_and_a_network_path_is_never_opened()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        const string Network = @"\\serveur\partage\profil";

        var folders = UserProfiles.WithRealPaths([temp, Network]);

        Assert.Equal(3, folders.Length);
        Assert.Contains(temp, folders);
        Assert.Contains(FinalPath.Of(temp)!, folders);
        Assert.Single(folders, f => f == Network);
    }

    [Fact]
    public void The_session_user_has_a_sid() => Assert.NotNull(new System.Security.Principal.SecurityIdentifier(SessionUser.Sid()!).Value);
}
