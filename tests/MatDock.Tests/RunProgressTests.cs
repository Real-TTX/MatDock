using MatDock.Core.Operations;
using MatDock.Core.Stacks;
using Xunit;

namespace MatDock.Tests;

/// <summary>
/// The progress of a run through a list (checking or updating stacks) and of a download: compose's JSON progress (here a real pull of an 84 MB layer) becomes
/// short readable lines again, and the entry that runs gets the download as a percentage.
/// </summary>
public class RunProgressTests
{
    private static string RealPull() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "compose-pull-progress.jsonl"));

    [Fact]
    public void Compose_JSON_progress_becomes_short_lines_with_a_download_line_per_tenth()
    {
        var json = RealPull();
        var lines = ComposeProgressLines.ConvertAll(json).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.InRange(lines.Length, 8, 40); // 279 JSON lines before
        Assert.Contains(lines, l => l.Trim() == "Image 127.0.0.1:5099/mdtest/app:latest Pulled");
        var downloads = lines.Where(l => l.Contains(" Downloading ", StringComparison.Ordinal)).ToList();
        Assert.InRange(downloads.Count, 3, 11);
        Assert.Contains(downloads, l => l.EndsWith("83.91MB/83.91MB", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith('{'));

        // The deploy history still counts the image as pulled.
        Assert.StartsWith("1 pulled", DeploymentHistory.Summarize(true, string.Join('\n', lines)));
    }

    [Fact]
    public void Pieces_of_lines_are_put_together_and_plain_output_passes_as_it_is()
    {
        var json = RealPull();
        var whole = ComposeProgressLines.ConvertAll(json);
        var pieces = new System.Text.StringBuilder();
        var lines = new ComposeProgressLines(t => pieces.Append(t));
        for (var i = 0; i < json.Length; i += 37)
        {
            lines.Write(json.Substring(i, Math.Min(37, json.Length - i)));
        }

        lines.Flush();
        Assert.Equal(whole, pieces.ToString());
        Assert.Equal(" Container shop-web-1  Recreated\n", ComposeProgressLines.ConvertAll(" Container shop-web-1  Recreated\n"));
    }

    [Fact]
    public void The_steps_say_how_far_the_run_is_and_the_running_one_shows_its_download()
    {
        var steps = new OperationSteps();
        steps.Add("1", "shop");
        steps.Add("2", "blog");
        steps.Set("1", StepState.Running, "Pulling the new images");

        // The output of the operation as it arrives (the converted pull), in pieces.
        var converted = ComposeProgressLines.ConvertAll(RealPull());
        var half = converted[..(converted.IndexOf("83.91MB/83.91MB", StringComparison.Ordinal) - 30)];
        steps.Feed(half);
        var running = steps.Snapshot().Single(s => s.Key == "1");
        Assert.NotNull(running.Percent);
        Assert.InRange(running.Percent!.Value, 1, 99);
        Assert.StartsWith("Downloading ", running.Detail);

        steps.Feed(converted[half.Length..]);
        Assert.Equal(100, steps.Snapshot().Single(s => s.Key == "1").Percent);

        steps.Set("1", StepState.Done, "Updated: web");
        steps.Set("2", StepState.Skipped, "Up to date");
        Assert.Equal(2, steps.Finished);
        Assert.Null(steps.Snapshot().Single(s => s.Key == "1").Percent);
        Assert.Equal("Updated: web", steps.Snapshot().Single(s => s.Key == "1").Detail);
    }
}
