using MatDock.Core.Auth;
using MatDock.Core.Stacks;
using Xunit;

namespace MatDock.Tests;

public class LoginThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Locks_after_the_limit_and_opens_again_when_the_window_has_passed()
    {
        var clock = new Clock();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < LoginThrottle.MaxFailuresPerAccount - 1; i++)
        {
            throttle.Failed("10.0.0.1", "Admin");
        }
        Assert.Null(throttle.RetryAfter("10.0.0.1", "admin"));

        throttle.Failed("10.0.0.1", "admin");
        Assert.Equal(LoginThrottle.Window, throttle.RetryAfter("10.0.0.1", "ADMIN"));

        // Another address is not affected by the account lock of this one.
        Assert.Null(throttle.RetryAfter("10.0.0.2", "admin"));

        clock.Now += LoginThrottle.Window;
        Assert.Null(throttle.RetryAfter("10.0.0.1", "admin"));
    }

    [Fact]
    public void One_address_trying_many_accounts_is_slowed_down_too()
    {
        var throttle = new LoginThrottle(new Clock());
        for (var i = 0; i < LoginThrottle.MaxFailuresPerAddress; i++)
        {
            throttle.Failed("10.0.0.9", "user" + i);
        }

        Assert.NotNull(throttle.RetryAfter("10.0.0.9", "someone-else"));
    }

    [Fact]
    public void A_successful_sign_in_forgets_the_failures_of_the_account()
    {
        var throttle = new LoginThrottle(new Clock());
        for (var i = 0; i < LoginThrottle.MaxFailuresPerAccount - 1; i++)
        {
            throttle.Failed("10.0.0.1", "admin");
        }

        throttle.Succeeded("10.0.0.1", "admin");
        throttle.Failed("10.0.0.1", "admin");
        Assert.Null(throttle.RetryAfter("10.0.0.1", "admin"));
    }
}

public class RepoFileTests
{
    [Fact]
    public void Files_of_the_clone_are_read_and_paths_out_of_it_are_refused()
    {
        var root = Directory.CreateTempSubdirectory("matdock-repo").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app"));
            File.WriteAllText(Path.Combine(root, "app", "compose.yml"), "services: {}");

            Assert.NotNull(StackService.RepoFile(root, Path.Combine(root, "app", "compose.yml")));
            Assert.Null(StackService.RepoFile(root, Path.Combine(root, "..", "secret")));
            Assert.Null(StackService.RepoFile(root, Path.Combine(root, "app", "..", "..", "x.png")));
            Assert.Null(StackService.RepoFile(root, Path.GetTempPath()));

            // A link in the repository (when the system lets us make one) is never followed.
            var link = Path.Combine(root, "app", "icon.png");
            try { File.CreateSymbolicLink(link, Path.Combine(Path.GetTempPath(), "outside.png")); }
            catch (Exception) { return; }
            Assert.Null(StackService.RepoFile(root, link));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
