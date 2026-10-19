using MatDock.Core.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

public class GitSshTests
{
    [Theory]
    [InlineData("https://github.com/org/repo.git", true, false)]
    [InlineData("ssh://git@github.com/org/repo.git", true, true)]
    [InlineData("ssh://git@git.example.org:2222/org/repo.git", true, true)]
    [InlineData("git@github.com:org/repo.git", true, true)]
    [InlineData("file:///etc/passwd", false, false)]
    [InlineData("/srv/repo", false, false)]
    [InlineData("C:\repo", false, false)]
    [InlineData("ssh://host", false, false)]
    public void Urls_are_https_or_ssh(string url, bool supported, bool ssh)
    {
        Assert.Equal(supported, GitUrls.IsSupported(url));
        Assert.Equal(ssh, GitUrls.IsSsh(url));
    }

    [Fact]
    public void The_scp_form_becomes_an_ssh_url_and_the_path_goes_to_upload_pack()
    {
        Assert.Equal("ssh://git@github.com/org/repo.git", GitUrls.ToSshUri("git@github.com:org/repo.git"));
        Assert.Equal("git-upload-pack '/org/repo.git'", SshGitTransport.UploadPackCommand(new Uri("ssh://git@github.com/org/repo.git")));
        Assert.Equal("git-upload-pack '~/repo.git'", SshGitTransport.UploadPackCommand(new Uri("ssh://git@host/~/repo.git")));
        Assert.Equal(@"git-upload-pack '/it'\''s.git'", SshGitTransport.UploadPackCommand(new Uri("ssh://git@host/it's.git")));
    }

    /// <summary>Against a real Git server over SSH, when MATDOCK_GIT_SSH_TEST is "url|private key file": clone with the key, the host key is
    /// reported, a wrong saved host key is refused.</summary>
    [Fact]
    public async Task A_repository_is_cloned_over_ssh_with_a_key()
    {
        var spec = Environment.GetEnvironmentVariable("MATDOCK_GIT_SSH_TEST");
        if (string.IsNullOrEmpty(spec))
        {
            return;
        }

        var (url, keyFile) = (spec.Split('|')[0], spec.Split('|')[1]);
        var git = new GitRepositoryService(NullLogger<GitRepositoryService>.Instance);
        var secret = new GitCredentialSecret(null, null) { PrivateKeyPem = File.ReadAllText(keyFile) };

        var scan = await git.CloneAndScanAsync(url, null, secret);
        try
        {
            Assert.Contains("app/docker-compose.yml", scan.ComposeFiles);
            Assert.NotNull(scan.CommitSha);
        }
        finally
        {
            GitRepositoryService.TryDelete(scan.WorkDir);
        }

        Assert.Equal(scan.CommitSha, await git.GetRemoteHeadShaAsync(url, null, secret));

        var wrong = secret with { HostKeyFingerprint = "SHA256:not-the-key" };
        var refused = await Assert.ThrowsAsync<GitOperationException>(() => git.CloneToTempAsync(url, null, wrong));
        Assert.Contains("host key", refused.Message);
    }
}
