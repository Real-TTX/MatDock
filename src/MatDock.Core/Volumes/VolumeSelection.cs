namespace MatDock.Core.Volumes;

/// <summary>Parses bulk-selection tokens of the form <c>"&lt;environmentId&gt;|&lt;volumeName&gt;"</c>.</summary>
public static class VolumeSelection
{
    public static IEnumerable<(long EnvId, string Volume)> Parse(IEnumerable<string>? tokens)
    {
        foreach (var token in tokens ?? Enumerable.Empty<string>())
        {
            if (token is null)
            {
                continue;
            }

            var sep = token.IndexOf('|');
            if (sep <= 0)
            {
                continue;
            }

            if (long.TryParse(token[..sep], out var envId))
            {
                var volume = token[(sep + 1)..];
                if (volume.Length > 0)
                {
                    yield return (envId, volume);
                }
            }
        }
    }
}
