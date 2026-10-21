using MatDock.Core.Execution;
using MatDock.Core.Operations;
using MatDock.Web.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

public class OperationRegistryTests
{
    private static OperationRegistry NewRegistry(TimeProvider? time = null)
        => new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OperationRegistry>.Instance, time);

    [Fact]
    public async Task Start_returns_at_once_and_the_work_completes_in_the_background()
    {
        using var registry = NewRegistry();
        var release = new TaskCompletionSource();

        var op = registry.Start("deploy", "app", null, async ctx =>
        {
            ctx.Log("==> step 1");
            await release.Task;
            return OperationOutcome.Success(new { changed = true });
        });

        Assert.Equal("deploy", op.Kind);
        Assert.Equal("app", op.Target);
        Assert.Same(op, registry.Get(op.Id));
        Assert.Equal(OperationStatus.Running, op.Status);

        release.SetResult();
        await registry.WaitAsync(op.Id);

        Assert.Equal(OperationStatus.Succeeded, op.Status);
        Assert.True(op.IsFinished);
        Assert.Null(op.Error);
        Assert.NotNull(op.FinishedUtc);
        Assert.Contains("changed", System.Text.Json.JsonSerializer.Serialize(op.Result));
    }

    [Fact]
    public void Output_is_read_incrementally_by_offset()
    {
        var op = new Operation("1", "deploy", "app", DateTime.UtcNow);
        op.Append(" Container app Creating\r\n");

        var (first, offset) = op.Read(0);
        Assert.Equal(" Container app Creating\n", first);

        op.Log("done");
        var (second, next) = op.Read(offset);
        Assert.Equal("done\n", second);
        Assert.Equal(op.Length, next);

        Assert.Equal(string.Empty, op.Read(next).Text);
        Assert.Equal(string.Empty, op.Read(99999).Text); // an offset past the end is clamped
        Assert.Equal(first + second, op.Read(-5).Text);
    }

    [Fact]
    public async Task Host_command_output_reaches_the_operation_through_the_ambient_sink()
    {
        using var registry = NewRegistry();
        Assert.Null(CommandOutput.Current);

        var op = registry.Start("pull", "app", null, ctx =>
        {
            CommandOutput.Current!(" Image nginx Pulled\n");
            return Task.FromResult(OperationOutcome.Success());
        });
        await registry.WaitAsync(op.Id);

        Assert.Equal(" Image nginx Pulled\n", op.Read(0).Text);
        Assert.Null(CommandOutput.Current); // the request's flow never sees the sink
    }

    [Fact]
    public async Task Failures_and_exceptions_end_the_operation_as_failed()
    {
        using var registry = NewRegistry();

        var failed = registry.Start("deploy", "a", null, _ => Task.FromResult(OperationOutcome.Failure("Deploy failed: no such image", new { changed = false })));
        var thrown = registry.Start("deploy", "b", null, _ => throw new InvalidOperationException("boom"));
        await registry.WaitAsync(failed.Id);
        await registry.WaitAsync(thrown.Id);

        Assert.Equal(OperationStatus.Failed, failed.Status);
        Assert.Equal("Deploy failed: no such image", failed.Error);
        Assert.Contains("no such image", failed.Read(0).Text); // the error is part of the output too
        Assert.NotNull(failed.Result);

        Assert.Equal(OperationStatus.Failed, thrown.Status);
        Assert.Contains("boom", thrown.Error);
    }

    [Fact]
    public async Task Work_with_the_same_lock_key_runs_one_after_the_other()
    {
        using var registry = NewRegistry();
        var release = new TaskCompletionSource();
        var secondStarted = false;

        var first = registry.Start("deploy", "app", "stack:1:app", async _ =>
        {
            await release.Task;
            return OperationOutcome.Success();
        });
        var second = registry.Start("delete", "app", "stack:1:app", _ =>
        {
            secondStarted = true;
            return Task.FromResult(OperationOutcome.Success());
        });
        var other = registry.Start("deploy", "other", "stack:1:other", _ => Task.FromResult(OperationOutcome.Success()));

        await registry.WaitAsync(other.Id);
        await Task.Delay(100);
        Assert.False(secondStarted);
        Assert.Contains("Waiting", second.Read(0).Text);

        release.SetResult();
        await registry.WaitAsync(first.Id);
        await registry.WaitAsync(second.Id);
        Assert.True(secondStarted);
        Assert.Equal(OperationStatus.Succeeded, second.Status);
    }

    [Fact]
    public async Task Work_with_the_same_lock_key_runs_in_the_order_it_was_started()
    {
        // The thread pool may start the background tasks in any order; the place in the queue is taken when the work is started.
        using var registry = NewRegistry();
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var operations = Enumerable.Range(0, 200)
            .Select(i => registry.Start("step", "app", "stack:1:app", _ =>
            {
                order.Enqueue(i);
                return Task.FromResult(OperationOutcome.Success());
            }))
            .ToList();

        foreach (var operation in operations)
        {
            await registry.WaitAsync(operation.Id);
        }

        Assert.Equal(Enumerable.Range(0, 200), order);
    }

    [Fact]
    public async Task Finished_operations_are_forgotten_after_the_retention()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        using var registry = NewRegistry(clock);
        var op = registry.Start("restart", "app", null, _ => Task.FromResult(OperationOutcome.Success()));
        await registry.WaitAsync(op.Id);

        clock.Now += TimeSpan.FromMinutes(59);
        Assert.NotNull(registry.Get(op.Id));

        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(registry.Get(op.Id));
    }

    [Fact]
    public async Task Running_operations_are_never_forgotten()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var registry = NewRegistry(clock);
        var release = new TaskCompletionSource();
        var op = registry.Start("update", "app", null, async _ => { await release.Task; return OperationOutcome.Success(); });

        clock.Now += TimeSpan.FromDays(1);
        Assert.NotNull(registry.Get(op.Id));

        release.SetResult();
        await registry.WaitAsync(op.Id);
    }

    [Fact]
    public async Task Api_dto_has_the_contract_fields()
    {
        using var registry = NewRegistry();
        var op = registry.Start("update", "app", null, ctx =>
        {
            ctx.Log("hello");
            return Task.FromResult(OperationOutcome.Success(new { changed = false }));
        });
        await registry.WaitAsync(op.Id);

        var json = System.Text.Json.JsonSerializer.Serialize(MatDockApi.ToDto(op, 0), ApiResults.Json);

        foreach (var field in new[] { "\"id\":", "\"kind\":\"update\"", "\"status\":\"succeeded\"", "\"output\":\"hello\\n\"",
                     "\"offset\":6", "\"finished\":true", "\"error\":null", "\"result\":{\"changed\":false}" })
        {
            Assert.Contains(field, json);
        }
    }
}
