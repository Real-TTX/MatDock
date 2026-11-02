using MatDock.Core.Ssh;
using MatDock.Core.Volumes;

namespace MatDock.Core.Stacks;

/// <summary>
/// The last step of a deploy, an update or a restart of the stack that runs MatDock itself. Done the ordinary way, compose stops MatDock's container to
/// replace it, and with it the process that runs compose: the new container is never made and MatDock stays down. So that step goes to a short-lived
/// helper container on the same Docker (made from MatDock's own image, which has the Docker CLI and compose): it waits a few seconds, so MatDock can
/// answer the page and write the history, then runs compose, writes its output and exit code beside the stack's files and goes. When compose fails, it
/// starts MatDock's old container again (if compose left it). Secrets reach it as environment variables of the helper (named with <c>-e</c>: docker takes
/// the values from its own environment, they are never on a command line).
/// </summary>
/// <param name="SelfId">MatDock's container (<see cref="Execution.SelfContainer"/>).</param>
/// <param name="DockerAccess">How the helper reaches Docker: with the local socket MatDock's own mounts (<c>--volumes-from</c>, which also brings the data
/// volume with the stack's files), over SSH the socket of the host.</param>
/// <param name="FilesInVolume">The stack's files are in MatDock's data volume (local socket); else the helper mounts the stack's folder of the host.</param>
public sealed record SelfHandover(string SelfId, string DockerAccess, bool FilesInVolume)
{
    /// <summary>What the output of a handed-over step ends with (the history, the update and the console look for it).</summary>
    public const string Marker = "MatDock restarts itself now";

    /// <summary>The output of the helper's compose, beside the stack's files.</summary>
    public const string LogFile = ".matdock-self-update.log";

    /// <summary>The exit code of the helper's compose, beside the stack's files.</summary>
    public const string ExitFile = ".matdock-self-update.exit";

    /// <summary>How long the helper waits before it acts.</summary>
    public const int DelaySeconds = 5;

    /// <summary>The name of the helper: one at a time (a second one while the first waits is refused by Docker).</summary>
    public const string HelperName = "matdock-self-update";

    /// <summary>
    /// The handover for an environment, or null when there is none: over a Docker API (tcp) the helper could not reach the stack's files, and over SSH
    /// with a DOCKER_HOST that is no unix socket it could not reach Docker.
    /// </summary>
    public static SelfHandover? For(SshConnectionSettings settings, string selfId)
    {
        if (selfId.Length != 64 || !selfId.All(Uri.IsHexDigit))
        {
            return null;
        }

        if (settings.IsLocal)
        {
            // The helper takes MatDock's mounts, the socket among them; a DOCKER_HOST of the environment (validated) is passed on.
            return new SelfHandover(selfId, "--volumes-from " + selfId
                + (string.IsNullOrEmpty(settings.DockerHost) || !VolumeCommands.IsValidDockerHost(settings.DockerHost) ? string.Empty : " -e DOCKER_HOST=" + settings.DockerHost),
                FilesInVolume: true);
        }

        if (settings.IsTcp)
        {
            return null;
        }

        var socket = string.IsNullOrEmpty(settings.DockerHost) ? "/var/run/docker.sock"
            : settings.DockerHost.StartsWith("unix://", StringComparison.Ordinal) && VolumeCommands.IsValidDockerHost(settings.DockerHost) ? settings.DockerHost["unix://".Length..]
            : null;
        return socket is null ? null : new SelfHandover(selfId, "-v " + socket + ":/var/run/docker.sock", FilesInVolume: false);
    }

    /// <summary>Why a step of MatDock's own stack cannot be handed over on this environment (<see cref="For"/> answered null).</summary>
    public const string NoHandover =
        "This stack runs MatDock itself, and over this connection MatDock cannot hand the restart to a helper container. Update it on the host "
        + "(docker compose pull && docker compose up -d), or manage it through the local Docker socket or SSH.";

    /// <summary>
    /// The shell fragment that hands the compose step over (it sits inside the single-quoted script bodies, so no single quote). <paramref name="composeArgs"/>
    /// are the words the script gives compose (<c>-p "$1" -f "$2" $ov $ef</c>: expanded by the script, one argument each), <paramref name="verb"/> what
    /// compose does (<c>up -d --remove-orphans</c>; constant text without <c>$</c> or quotes). Needs <c>$dir</c> (the stack's folder) set.
    /// </summary>
    public string Compose(string dockerHead, string composeArgs, string verb)
    {
        if (verb.Any(c => c is '$' or '"' or '\'' or '\\' or '`'))
        {
            throw new ArgumentException("The verb is constant text.", nameof(verb));
        }

        var files = FilesInVolume ? string.Empty : " -v \"$dir\":\"$dir\"";
        return Image(dockerHead) + EnvArgs
               + "rm -f \"$dir/" + LogFile + "\" \"$dir/" + ExitFile + "\"; "
               + dockerHead + " run -d --rm --name " + HelperName + " --no-healthcheck " + DockerAccess + files + " $envargs --entrypoint sh \"$img\" -c "
               + "\"sleep " + DelaySeconds + "; cd \\\"\\$0\\\" && docker compose \\\"\\$@\\\" " + verb + " > " + LogFile + " 2>&1; rc=\\$?; "
               + "[ \\$rc -eq 0 ] || docker start " + SelfId + " >> " + LogFile + " 2>&1; echo \\$rc > " + ExitFile + "\" "
               + "\"$dir\" " + composeArgs + " > /dev/null; "
               + "echo \"" + Marker + ": a helper container runs docker compose " + verb + " in " + DelaySeconds + " s.\"";
    }

    /// <summary>
    /// A restart through the helper: of MatDock's own container, or with <paramref name="project"/> (validated) of all containers of that compose project.
    /// A whole command, not a fragment.
    /// </summary>
    public string Restart(string dockerHead, string? project = null)
    {
        if (project is not null && !StackCommands.IsValidName(project))
        {
            throw new ArgumentException($"Invalid project name: '{project}'.", nameof(project));
        }

        var what = project is null ? SelfId : "\\$(docker ps -q --filter label=com.docker.compose.project=" + project + ")";
        var script = Image(dockerHead)
                     + dockerHead + " run -d --rm --name " + HelperName + " --no-healthcheck " + DockerAccess + " --entrypoint sh \"$img\" -c "
                     + "\"sleep " + DelaySeconds + "; docker restart " + what + "\" > /dev/null; "
                     + "echo \"" + Marker + ": a helper container restarts it in " + DelaySeconds + " s.\"";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script) + " 2>&1";
    }

    /// <summary>
    /// The compose project and service of MatDock's container on an environment, as <c>project|service</c> (empty parts when it was not made by compose);
    /// fails with "No such object" when the container is not on it.
    /// </summary>
    public static string Inspect(string dockerHead, string selfId)
    {
        if (selfId.Length != 64 || !selfId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Not a container id.", nameof(selfId));
        }

        return VolumeCommands.PathPrefix + dockerHead + " inspect -f "
               + VolumeFileCommands.ShellQuote("{{index .Config.Labels \"com.docker.compose.project\"}}|{{index .Config.Labels \"com.docker.compose.service\"}}")
               + " " + selfId + " 2>&1";
    }

    /// <summary>
    /// What the helper left beside the files of the stack <paramref name="name"/>: its exit code on the first line, then compose's output; both files are
    /// removed. Exit 3 while the helper has not finished.
    /// </summary>
    public static string ReadResult(string name)
    {
        if (!StackCommands.IsValidName(name))
        {
            throw new ArgumentException($"Invalid stack name: '{name}'.", nameof(name));
        }

        var script = "dir=\"" + StackCommands.StacksDir + "/$1\"; if [ -f \"$dir/" + ExitFile + "\" ]; then cat \"$dir/" + ExitFile + "\"; "
                     + "cat \"$dir/" + LogFile + "\" 2>/dev/null || true; rm -f \"$dir/" + ExitFile + "\" \"$dir/" + LogFile + "\"; else exit 3; fi";
        return VolumeCommands.PathPrefix + "sh -c " + VolumeFileCommands.ShellQuote(script) + " sh " + VolumeFileCommands.ShellQuote(name);
    }

    /// <summary>Whether the output of a step says it was handed over.</summary>
    public static bool HandedOver(string? output) => output is not null && output.Contains(Marker, StringComparison.Ordinal);

    /// <summary>
    /// MatDock's own image, by the reference it was started from (after a pull the new image of that tag: it has the same tools). Not by its id first:
    /// once a pull or a build took its tag the old image is dangling, and Docker's containerd store no longer finds it by id. The id when the
    /// reference is gone.
    /// </summary>
    private string Image(string dockerHead)
        => "img=$(" + dockerHead + " inspect -f \"{{.Config.Image}}\" " + SelfId + "); "
           + dockerHead + " image inspect \"$img\" > /dev/null 2>&1 || img=$(" + dockerHead + " inspect -f \"{{.Image}}\" " + SelfId + "); ";

    /// <summary>The names of the exported variables (<c>$xs</c>, the secrets) as <c>-e NAME</c>: docker takes their values from its own environment.</summary>
    private const string EnvArgs =
        "envargs=\"\"; if [ -n \"${xs:-}\" ]; then nl=$(printf \"\\n_\"); nl=${nl%_}; oifs=$IFS; IFS=$nl; set -f; "
        + "for kv in $xs; do envargs=\"$envargs -e ${kv%%=*}\"; done; set +f; IFS=$oifs; fi; ";
}
