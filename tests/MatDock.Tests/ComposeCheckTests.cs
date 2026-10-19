using MatDock.Core.Stacks;
using Xunit;

namespace MatDock.Tests;

public class ComposeCheckTests
{
    [Theory]
    [InlineData("services:\n  app:\n    image: nginx:alpine\n")]
    [InlineData("services:\n  app:\n    build: .\n  worker:\n    extends:\n      service: app\n")]
    [InlineData("include:\n  - other.yml\n")]
    public void Accepts_compose_files(string yaml) => Assert.Null(ComposeCheck.Problem(yaml));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("- a\n- b\n", "map")]
    [InlineData("version: '3'\n", "no 'services:'")]
    [InlineData("services:\n  app:\n    image: nginx\n   ports: [80]\n", "line 4")]
    [InlineData("services:\n  app:\n    restart: always\n", "Service 'app' (line 2) needs an 'image' or a 'build'")]
    [InlineData("services:\n  app: nginx\n", "Service 'app' (line 2) is not a map")]
    public void Finds_the_mistake(string yaml, string expected)
        => Assert.Contains(expected, ComposeCheck.Problem(yaml));
}
