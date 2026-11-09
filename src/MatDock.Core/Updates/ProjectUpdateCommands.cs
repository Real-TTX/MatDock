using System.Text.RegularExpressions;
using MatDock.Core.Stacks;
using MatDock.Core.Volumes;

namespace MatDock.Core.Updates;

/// <summary>
/// Where docker compose keeps a project MatDock did not deploy (a discovered one): its folder, its compose files and its env files, as the labels of its
/// containers name them. Only absolute paths without <c>..</c>, spaces or quotes are taken: they go into a mount and a shell line.
/// </summary>
public sealed partial record ComposeProjectFiles(string WorkingDir, IReadOnlyList<string> ConfigFiles, IReadOnlyList<string> EnvFiles)
{
    public const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    public const string ConfigFilesLabel = "com.docker.compose.project.config_files";
    public const string EnvFilesLabel = "com.docker.compose.project.environment_file";

    /// <summary>The files of a project from the labels of one of its containers; null when compose did not name them (or not in a usable way).</summary>
    public static ComposeProjectFiles? FromLabels(IReadOnlyDictionary<string, string> labels)
    {
        if (!labels.TryGetValue(WorkingDirLabel, out var wd) || !IsSafePath(wd = wd.Trim().TrimEnd('/')))
        {
            return null;
        }

        var configs = Split(labels.GetValueOrDefault(ConfigFilesLabel)).Select(f => f.StartsWith('/') ? f : $"{wd}/{f}").ToList();
        var envs = Split(labels.GetValueOrDefault(EnvFilesLabel)).Select(f => f.StartsWith('/') ? f : $"{wd}/{f}").ToList();
        return configs.Count > 0 && configs.All(IsSafePath) && envs.All(IsSafePath)
            ? new ComposeProjectFiles(wd, configs, envs)
            : null;
    }

    /// <summary>The folders a helper must see: the project folder and the folders of the files outside it (each only once, none inside another).</summary>
    public IReadOnlyList<string> Folders()
    {
        var all = ConfigFiles.Concat(EnvFiles).Select(Dir).Prepend(WorkingDir).Distinct(StringComparer.Ordinal).ToList();
        return all.Where(d => !all.Any(o => o != d && (o == "/" || d.StartsWith(o + "/", StringComparison.Ordinal)))).ToList();
    }

    private static string Dir(string file) => file[..Math.Max(1, file.LastIndexOf('/'))];

    private static IEnumerable<string> Split(string? value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsSafePath(string path)
        => SafePath().IsMatch(path) && !path.Split('/').Any(s => s is ".." or ".");

    [GeneratedRegex(@"^/[A-Za-z0-9._@+=\-/]*\z")]
    private static partial Regex SafePath();
}

/// <summary>
/// Updates a discovered compose project in place, with its own files: a short-lived container with the Docker CLI (<see cref="Image"/>) sees the project
/// folder at the same path (read-only) and the Docker socket, and runs <c>docker compose pull</c> and <c>up -d --no-deps</c> for the given services. So
/// the files stay where they are (nothing is copied or changed), relative paths resolve as they did, and it works the same over SSH and on the local host.
/// </summary>
public static class ProjectUpdateCommands
{
    /// <summary>The image with the Docker CLI and the compose plugin.</summary>
    public const string Image = "docker:cli";

    /// <summary>What Docker says when a folder to mount is not on the host (the compose files live in another container, or are gone).</summary>
    public const string MissingFolder = "bind source path does not exist";

    /// <summary>
    /// The helper run: <paramref name="pull"/> pulls the services first (an update), without it only <c>up -d</c> (a rollback onto images tagged back).
    /// Null when Docker is not reached through a socket on the host (a TCP <c>DOCKER_HOST</c>), so a helper there could not reach it.
    /// </summary>
    public static string? Run(ComposeProjectFiles files, string project, IReadOnlyList<string> services, bool pull, string dockerHead, string? dockerHost, string name, string image = Image)
    {
        if (!StackCommands.IsValidName(project) || services.Count == 0 || !services.All(StackCommands.IsValidServiceName))
        {
            throw new ArgumentException("Invalid project or service name.");
        }

        var socket = dockerHost is null or "" ? "/var/run/docker.sock"
            : dockerHost.StartsWith("unix://", StringComparison.Ordinal) ? dockerHost["unix://".Length..] : null;
        if (socket is null || !ComposeProjectFiles.IsSafePath(socket))
        {
            return null;
        }

        string Q(string s) => VolumeFileCommands.ShellQuote(s);
        var compose = "docker compose -p " + Q(project) + " --project-directory " + Q(files.WorkingDir)
            + string.Concat(files.ConfigFiles.Select(f => " -f " + Q(f)))
            + string.Concat(files.EnvFiles.Select(f => " --env-file " + Q(f)));
        var list = string.Concat(services.Select(s => " " + Q(s)));
        var script = (pull ? compose + " pull" + list + " && " : string.Empty) + compose + " up -d --no-deps" + list;
        var mounts = string.Concat(files.Folders().Select(d => " --mount " + Q($"type=bind,src={d},dst={d},readonly")));
        return VolumeCommands.PathPrefix + dockerHead + " run --rm --log-driver none --name " + Q(name)
            + " -v " + Q(socket + ":/var/run/docker.sock") + mounts + " -w " + Q(files.WorkingDir)
            + " --entrypoint sh " + Q(image) + " -c " + Q(script) + " 2>&1";
    }

    /// <summary>A name for the helper: <c>matdock-update-&lt;project&gt;-&lt;6 hex&gt;</c>.</summary>
    public static string HelperName(string project)
        => $"matdock-update-{(project.Length > 40 ? project[..40] : project)}-{Guid.NewGuid().ToString("N")[..6]}";
}
