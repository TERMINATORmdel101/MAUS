using System.Security.Cryptography;
using System.Text;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Workshop;

public class PresentMonCaptureTests
{
    [Fact]
    public void Verified_file_stays_locked_against_writing_renaming_and_deletion_until_closed()
    {
        var folder = Directory.CreateTempSubdirectory("maus-presentmon-");
        try
        {
            var path = Path.Combine(folder.FullName, "PresentMon.exe");
            File.WriteAllText(path, "copie d'essai");
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("copie d'essai")));

            using (var locked = PresentMonCapture.OpenVerified(path, hash))
            {
                Assert.NotNull(locked);
                Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "remplacé"));
                Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".old"));
                Assert.ThrowsAny<IOException>(() => File.Delete(path));
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.True(reader.CanRead);
            }

            File.WriteAllText(path, "remplacé");
            Assert.Null(PresentMonCapture.OpenVerified(path, hash));
            File.Delete(path);
            Assert.Null(PresentMonCapture.OpenVerified(path, hash));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
