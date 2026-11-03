using System.Globalization;
using System.Text.RegularExpressions;

namespace MatDock.Core.Operations;

public enum StepState
{
    Waiting,
    Running,
    Done,
    Failed,
    Skipped,
}

/// <summary>One entry of a run (a stack to check or to update): its state, a line about it, and how far its download is (0–100) while images are pulled.</summary>
public sealed record StepView(string Key, string Label, StepState State, string? Detail, double? Percent);

/// <summary>
/// The entries of an operation that works through a list (the updates of many stacks): what waits, what runs, what is done, so a page can show a progress
/// bar and the list with a state per entry. While an entry runs, the output of the operation is read for Docker's download lines
/// (<c>a1b2c3d4e5f6 Downloading [==&gt; ] 12.3MB/45.6MB</c>), so its download shows as a bar of its own.
/// </summary>
public sealed partial class OperationSteps
{
    private readonly object _gate = new();
    private readonly List<Step> _steps = [];
    private readonly Dictionary<string, (double Current, double Total)> _layers = new(StringComparer.Ordinal);
    private string _partial = string.Empty;

    private sealed class Step(string key, string label)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public StepState State { get; set; }
        public string? Detail { get; set; }
        public double? Percent { get; set; }
    }

    public void Add(string key, string label)
    {
        lock (_gate)
        {
            _steps.Add(new Step(key, label));
        }
    }

    public void Set(string key, StepState state, string? detail = null)
    {
        lock (_gate)
        {
            if (_steps.FirstOrDefault(s => s.Key == key) is not { } step)
            {
                return;
            }

            step.State = state;
            step.Detail = detail ?? (state == StepState.Running ? step.Detail : null);
            if (state != StepState.Running)
            {
                step.Percent = null;
            }

            if (state == StepState.Running)
            {
                _layers.Clear();
            }
        }
    }

    public IReadOnlyList<StepView> Snapshot()
    {
        lock (_gate)
        {
            return _steps.Select(s => new StepView(s.Key, s.Label, s.State, s.Detail, s.Percent)).ToList();
        }
    }

    public int Total { get { lock (_gate) { return _steps.Count; } } }

    public int Finished { get { lock (_gate) { return _steps.Count(s => s.State is StepState.Done or StepState.Failed or StepState.Skipped); } } }

    // "a1b2c3d4e5f6 Downloading [==>    ]  12.3MB/45.6MB" (compose and docker pull without a terminal), and the end of a layer.
    [GeneratedRegex(@"(?<layer>[0-9a-f]{12})\s+Downloading\s+(?:\[[^\]]*\]\s+)?(?<cur>[\d.]+)\s*(?<cu>[kKMGT]?B)\s*/\s*(?<tot>[\d.]+)\s*(?<tu>[kKMGT]?B)")]
    private static partial Regex DownloadLine();

    [GeneratedRegex(@"(?<layer>[0-9a-f]{12})\s+(?:Download complete|Pull complete|Already exists)")]
    private static partial Regex LayerDone();

    /// <summary>Reads output of the operation for the download of the running entry.</summary>
    public void Feed(string text)
    {
        lock (_gate)
        {
            if (_steps.FirstOrDefault(s => s.State == StepState.Running) is not { } step)
            {
                _partial = string.Empty;
                return;
            }

            var all = _partial + text.Replace('\r', '\n');
            var end = all.LastIndexOf('\n');
            _partial = end < 0 ? all : all[(end + 1)..];
            if (_partial.Length > 4096)
            {
                _partial = string.Empty;
            }

            if (end < 0)
            {
                return;
            }

            var changed = false;
            foreach (var line in all[..end].Split('\n'))
            {
                if (DownloadLine().Match(line) is { Success: true } m)
                {
                    _layers[m.Groups["layer"].Value] = (Bytes(m.Groups["cur"].Value, m.Groups["cu"].Value), Bytes(m.Groups["tot"].Value, m.Groups["tu"].Value));
                    changed = true;
                }
                else if (LayerDone().Match(line) is { Success: true } d && _layers.TryGetValue(d.Groups["layer"].Value, out var layer))
                {
                    _layers[d.Groups["layer"].Value] = (layer.Total, layer.Total);
                    changed = true;
                }
            }

            if (changed)
            {
                var current = _layers.Values.Sum(l => l.Current);
                var total = _layers.Values.Sum(l => l.Total);
                if (total > 0)
                {
                    step.Percent = Math.Min(100, current * 100 / total);
                    step.Detail = $"Downloading {Size(current)} of {Size(total)}";
                }
            }
        }
    }

    private static double Bytes(string value, string unit)
    {
        var n = double.Parse(value, CultureInfo.InvariantCulture);
        return n * (unit.ToUpperInvariant() switch { "KB" => 1e3, "MB" => 1e6, "GB" => 1e9, "TB" => 1e12, _ => 1 });
    }

    private static string Size(double bytes) => bytes >= 1e9 ? $"{bytes / 1e9:0.0} GB" : bytes >= 1e6 ? $"{bytes / 1e6:0.#} MB" : $"{bytes / 1e3:0} kB";
}
