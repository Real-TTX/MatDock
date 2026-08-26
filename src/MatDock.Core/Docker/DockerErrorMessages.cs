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
        SshAuthenticationException => "Authentifizierung fehlgeschlagen. Bei Benutzer 'root' ist der Passwort-Login "
            + "oft gesperrt (sshd: PermitRootLogin prohibit-password / PasswordAuthentication no) – dann SSH-Key "
            + "verwenden oder einen Benutzer der Gruppe 'docker'.",
        SshConnectionException => "SSH-Verbindung fehlgeschlagen.",
        System.Net.Sockets.SocketException => "Host nicht erreichbar (Adresse/Port prüfen).",
        _ => $"Fehler: {Innermost(ex).Message}"
    };

    /// <summary>Adds a hint (permission/daemon/not-found) to the real remote Docker error output.</summary>
    public static string InterpretDockerError(string? stderr, string? stdout)
    {
        var detail = FirstLine(stderr) ?? FirstLine(stdout) ?? "keine Fehlerausgabe";
        var lower = detail.ToLowerInvariant();

        string? hint = null;
        if (lower.Contains("permission denied") || lower.Contains("got permission denied"))
        {
            hint = "Keine Berechtigung für den Docker-Socket. Der SSH-Benutzer muss den Docker-Daemon erreichen dürfen "
                 + "(Benutzer in Gruppe \"docker\", oder – bei Rootless-Docker – als der Docker-Besitzer verbinden). ";
        }
        else if (lower.Contains("cannot connect to the docker daemon") || lower.Contains("is the docker daemon running")
                 || lower.Contains("connection refused"))
        {
            hint = "Der Docker-Daemon ist nicht erreichbar. Läuft der Docker-Dienst (ggf. Rootless-Socket)? ";
        }
        else if (lower.Contains("not found") || lower.Contains("no such file"))
        {
            hint = "Docker wurde nicht gefunden. Ist Docker installiert und im PATH des SSH-Benutzers? ";
        }

        // Always surface the real remote error so per-host causes are diagnosable.
        return hint is null
            ? $"Docker-Fehler: {detail}"
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
