using System.Text;

namespace MatDock.Core.Operations;

public enum OperationStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2
}

/// <summary>What a finished piece of work reports back: success, an error text and an optional result object.</summary>
public sealed record OperationOutcome(bool Ok, string? Error, object? Result)
{
    public static OperationOutcome Success(object? result = null) => new(true, null, result);

    public static OperationOutcome Failure(string error, object? result = null) => new(false, error, result);
}

/// <summary>
/// One long-running action (deploy, update, backup, …) started through the API. Its output grows while it runs and
/// is read incrementally by character offset. Thread-safe: the worker appends while API requests read.
/// </summary>
public sealed class Operation
{
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private OperationStatus _status = OperationStatus.Running;
    private string? _error;
    private object? _result;
    private DateTime? _finishedUtc;

    public Operation(string id, string kind, string? target, DateTime startedUtc)
    {
        Id = id;
        Kind = kind;
        Target = target;
        StartedUtc = startedUtc;
    }

    public string Id { get; }

    /// <summary>What runs, e.g. <c>deploy</c>, <c>update</c>, <c>backup</c>.</summary>
    public string Kind { get; }

    /// <summary>The object it runs on (a stack name), if any.</summary>
    public string? Target { get; }

    public DateTime StartedUtc { get; }

    public OperationStatus Status { get { lock (_gate) { return _status; } } }

    public bool IsFinished => Status != OperationStatus.Running;

    public string? Error { get { lock (_gate) { return _error; } } }

    public object? Result { get { lock (_gate) { return _result; } } }

    public DateTime? FinishedUtc { get { lock (_gate) { return _finishedUtc; } } }

    public int Length { get { lock (_gate) { return _output.Length; } } }

    /// <summary>Appends raw output text (as produced by a command, line breaks included).</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _output.Append(text.Replace("\r\n", "\n"));
        }
    }

    /// <summary>Appends one line (a status message of the operation itself).</summary>
    public void Log(string line)
    {
        lock (_gate)
        {
            if (_output.Length > 0 && _output[^1] != '\n')
            {
                _output.Append('\n');
            }

            _output.Append(line.Replace("\r\n", "\n")).Append('\n');
        }
    }

    /// <summary>The output after <paramref name="offset"/> characters and the offset to continue from.</summary>
    public (string Text, int NextOffset) Read(int offset)
    {
        lock (_gate)
        {
            var from = Math.Clamp(offset, 0, _output.Length);
            return (_output.ToString(from, _output.Length - from), _output.Length);
        }
    }

    /// <summary>Marks the operation finished with the given outcome (only the first call counts).</summary>
    public void Complete(OperationOutcome outcome, DateTime finishedUtc)
    {
        lock (_gate)
        {
            if (_status != OperationStatus.Running)
            {
                return;
            }

            _status = outcome.Ok ? OperationStatus.Succeeded : OperationStatus.Failed;
            _error = outcome.Ok ? null : (string.IsNullOrWhiteSpace(outcome.Error) ? "Failed." : outcome.Error);
            _result = outcome.Result;
            _finishedUtc = finishedUtc;
        }
    }
}
