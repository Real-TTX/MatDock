using System.Collections.Concurrent;
using MatDock.Core.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Operations;

/// <summary>What a piece of work started by <see cref="OperationRegistry.Start"/> gets: its operation (for log lines),
/// a DI scope of its own and a token that is cancelled when MatDock shuts down.</summary>
public sealed class OperationContext
{
    public OperationContext(Operation operation, IServiceProvider services, CancellationToken cancellationToken)
    {
        Operation = operation;
        Services = services;
        CancellationToken = cancellationToken;
    }

    public Operation Operation { get; }

    public IServiceProvider Services { get; }

    public CancellationToken CancellationToken { get; }

    public void Log(string line) => Operation.Log(line);
}

/// <summary>
/// In-memory registry of the asynchronous API operations (singleton). <see cref="Start"/> runs the work in the
/// background on its own DI scope and returns at once; the host commands of the work stream their output into the
/// operation (<see cref="CommandOutput"/>). Work with the same lock key (one stack) runs one after the other.
/// Finished operations are forgotten after <see cref="Retention"/>; nothing survives a restart.
/// </summary>
public sealed class OperationRegistry : IDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OperationRegistry> _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _shutdown = new();

    public OperationRegistry(IServiceScopeFactory scopeFactory, ILogger<OperationRegistry> logger, TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long a finished operation stays readable.</summary>
    public TimeSpan Retention { get; set; } = DefaultRetention;

    /// <summary>Starts <paramref name="work"/> in the background and returns its (running) operation.</summary>
    /// <param name="kind">What runs, e.g. <c>deploy</c>.</param>
    /// <param name="target">The object it runs on (a stack name), if any.</param>
    /// <param name="lockKey">Work with the same key never runs concurrently (null = no lock).</param>
    /// <param name="work">The work; it reports its outcome, an exception counts as a failure.</param>
    public Operation Start(string kind, string? target, string? lockKey, Func<OperationContext, Task<OperationOutcome>> work)
    {
        Sweep();

        var operation = new Operation(Guid.NewGuid().ToString("N"), kind, target, _time.GetUtcNow().UtcDateTime);
        _operations[operation.Id] = operation;

        // Do not let the request's execution context (HttpContext, user) flow into the background work.
        Task task;
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(() => RunAsync(operation, lockKey, work));
        }

        _running[operation.Id] = task;
        _ = task.ContinueWith(_ => _running.TryRemove(operation.Id, out Task? _), TaskScheduler.Default);
        return operation;
    }

    /// <summary>The operation with this id, or null when unknown or already forgotten.</summary>
    public Operation? Get(string id)
    {
        Sweep();
        return _operations.TryGetValue(id, out var operation) ? operation : null;
    }

    /// <summary>Waits until the operation's work has ended (tests, shutdown).</summary>
    public Task WaitAsync(string id)
        => _running.TryGetValue(id, out var task) ? task : Task.CompletedTask;

    /// <summary>Forgets finished operations older than <see cref="Retention"/>.</summary>
    public void Sweep()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - Retention;
        foreach (var (id, operation) in _operations)
        {
            if (operation.FinishedUtc is { } finished && finished < cutoff)
            {
                _operations.TryRemove(id, out _);
            }
        }
    }

    private async Task RunAsync(Operation operation, string? lockKey, Func<OperationContext, Task<OperationOutcome>> work)
    {
        var token = _shutdown.Token;
        SemaphoreSlim? gate = null;
        OperationOutcome outcome;
        try
        {
            if (lockKey is not null)
            {
                gate = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
                if (!await gate.WaitAsync(0, token))
                {
                    operation.Log("Waiting for another operation on the same object to finish …");
                    await gate.WaitAsync(token);
                }
            }

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                using var output = CommandOutput.Redirect(operation.Append);
                outcome = await work(new OperationContext(operation, scope.ServiceProvider, token));
            }
            finally
            {
                gate?.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = OperationOutcome.Failure("Cancelled: MatDock is shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Operation {Kind} ({Target}) failed.", operation.Kind, operation.Target);
            outcome = OperationOutcome.Failure($"Error: {ex.Message}");
        }

        if (!outcome.Ok && !string.IsNullOrWhiteSpace(outcome.Error))
        {
            operation.Log(outcome.Error);
        }

        operation.Complete(outcome, _time.GetUtcNow().UtcDateTime);
    }

    // Only cancelled, never disposed: work that is still winding down may observe the token.
    public void Dispose() => _shutdown.Cancel();
}
