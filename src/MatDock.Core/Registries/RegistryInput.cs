namespace MatDock.Core.Registries;

/// <summary>Form carrier for creating/editing a container registry. Token blank on edit keeps the stored one.</summary>
public sealed class RegistryInput
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Token { get; set; }
    public bool Insecure { get; set; }
    public bool Enabled { get; set; } = true;
}
