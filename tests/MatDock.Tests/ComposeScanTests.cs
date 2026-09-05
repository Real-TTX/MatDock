using MatDock.Core.Git;
using Xunit;

namespace MatDock.Tests;

public class ComposeScanTests
{
    [Fact]
    public void ScanComposeFiles_finds_compose_variants_and_skips_noise()
    {
        var root = Path.Combine(Path.GetTempPath(), "matdock-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "apps", "web"));
            Directory.CreateDirectory(Path.Combine(root, "apps", "db"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));

            // Should be discovered:
            File.WriteAllText(Path.Combine(root, "docker-compose.yml"), "x");
            File.WriteAllText(Path.Combine(root, "compose.yaml"), "x");
            File.WriteAllText(Path.Combine(root, "apps", "web", "docker-compose.yml"), "x");
            File.WriteAllText(Path.Combine(root, "apps", "db", "compose.prod.yml"), "x");
            // Should be ignored:
            File.WriteAllText(Path.Combine(root, "readme.md"), "x");
            File.WriteAllText(Path.Combine(root, "values.yaml"), "x");
            File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "docker-compose.yml"), "x");

            var found = GitRepositoryService.ScanComposeFiles(root);

            Assert.Contains("docker-compose.yml", found);
            Assert.Contains("compose.yaml", found);
            Assert.Contains("apps/web/docker-compose.yml", found);   // POSIX separators
            Assert.Contains("apps/db/compose.prod.yml", found);
            Assert.DoesNotContain("readme.md", found);
            Assert.DoesNotContain("values.yaml", found);
            Assert.DoesNotContain(found, p => p.Contains("node_modules"));
        }
        finally
        {
            GitRepositoryService.TryDelete(root);
        }
    }
}
