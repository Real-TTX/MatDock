using MatDock.Core.Entities;

namespace MatDock.Core.Environments;

/// <summary>How the API, MCP and a bridge name a connection type.</summary>
public static class ConnectionNames
{
    public static string Of(ConnectionType type) => type switch
    {
        ConnectionType.Local => "local",
        ConnectionType.Tcp => "tcp",
        ConnectionType.Bridge => "bridge",
        _ => "ssh",
    };
}
