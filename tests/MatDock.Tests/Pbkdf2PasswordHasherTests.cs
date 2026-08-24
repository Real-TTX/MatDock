using MatDock.Core.Security;
using Xunit;

namespace MatDock.Tests;

public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_then_Verify_roundtrips()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.True(_hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var hash = _hasher.Hash("s3cret");
        Assert.False(_hasher.Verify("S3cret", hash));
        Assert.False(_hasher.Verify("wrong", hash));
    }

    [Fact]
    public void Hash_is_salted_and_differs_each_time()
    {
        var a = _hasher.Hash("same");
        var b = _hasher.Hash("same");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_has_expected_format()
    {
        var parts = _hasher.Hash("x").Split('$');
        Assert.Equal(5, parts.Length);
        Assert.Equal("pbkdf2", parts[0]);
        Assert.Equal("sha256", parts[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2$sha256$abc$def")]      // too few segments
    [InlineData("pbkdf2$sha256$0$AAAA$AAAA")]  // zero iterations
    public void Verify_returns_false_for_malformed_hash(string stored)
    {
        Assert.False(_hasher.Verify("whatever", stored));
    }
}
