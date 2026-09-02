using Renci.SshNet.Common;

namespace MatDock.Core.Docker;

/// <summary>
/// Translates raw SSH/Docker failures into clear, actionable German messages. Shared by every
/// service that talks to a remote Docker host over SSH so the UI never surfaces an opaque
/// .NET/socket error where a helpful hint (auth, missing daemon, socket permission) is possible.
/// </summary>
public static class DockerErrorMessages
{
    /// <summary>Maps an SSH-layer exception (connect/auth/socket) to an actionable message.</summary>
    public static string DescribeSshError(Exception ex) => ex switch
    {
        SshAuthenticationException => "Authentication failed. For the 'root' user, password login is "
            + "often disabled (sshd: PermitRootLogin prohibit-password / PasswordAuthentication no) - use an SSH key "
            + "instead, or a user in the 'docker' group.",
        SshConnectionException => "SSH connection failed.",
        System.Net.Sockets.SocketException => "Host unreachable (check address/port).",
        _ => $"Error: {Innermost(ex).Message}"
    };

    /// <summary>Adds a hint (permission/daemon/not-found) to the real remote Docker error output.</summary>
    public static string InterpretDockerError(string? stderr, string? stdout)
    {
        var detail = FirstLine(stderr) ?? FirstLine(stdout) ?? "no error output";
        var lower = detail.ToLowerInvariant();

        string? hint = null;
        if (lower.Contains("permission denied") || lower.Contains("got permission denied"))
        {
            hint = "No permission for the Docker socket. The SSH user must be allowed to reach the Docker daemon "
                 + "(a user in the \"docker\" group, or - with rootless Docker - connect as the Docker owner). ";
        }
        else if (lower.Contains("cannot connect to the docker daemon") || lower.Contains("is the docker daemon running")
                 || lower.Contains("connection refused"))
        {
            hint = "The Docker daemon is not reachable. Is the Docker service running (possibly a rootless socket)? ";
        }
        else if (lower.Contains("not found") || lower.Contains("no such file"))
        {
            hint = "Docker was not found. Is Docker installed and on the SSH user's PATH? ";
        }

        // Always surface the real remote error so per-host causes are diagnosable.
        return hint is null
            ? $"Docker error: {detail}"
            : $"{hint}Details: {detail}";
    }

    /// <summary>True when the exception originates from the SSH layer (connect/auth/socket).</summary>
    public static bool IsSshError(Exception ex)
        => ex is SshException or System.Net.Sockets.SocketException;

    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }
}
