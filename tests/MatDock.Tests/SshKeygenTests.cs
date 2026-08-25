using System.Text;
using MatDock.Core.Ssh;
using Renci.SshNet;
using Xunit;

namespace MatDock.Tests;

public class SshKeygenTests
{
    [Fact]
    public void Generate_produces_openssh_public_and_loadable_private_key()
    {
        var kp = SshKeygen.Generate("matdock@example");

        Assert.StartsWith("ssh-rsa ", kp.PublicKeyOpenSsh);
        Assert.EndsWith(" matdock@example", kp.PublicKeyOpenSsh);
        Assert.Contains("BEGIN RSA PRIVATE KEY", kp.PrivateKeyPem);

        // The generated private key must be usable by the SSH client MatDock actually connects with.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kp.PrivateKeyPem));
        var ex = Record.Exception(() => new PrivateKeyFile(stream));
        Assert.Null(ex);
    }
}
