namespace MatDock.Core.Registries;

/// <summary>Resolved registry credentials (decrypted) handed to the API client / login — never persisted or logged.</summary>
public sealed record RegistryLogin(string Host, string Username, string? Token, bool Insecure);

/// <summary>Outcome of a registry operation (test/browse).</summary>
public sealed record RegistryResult(bool Ok, string Message)
{
    public static RegistryResult Fail(string message) => new(false, message);
    public static RegistryResult Success(string message) => new(true, message);
}

/// <summary>A page of repository names from a registry catalog.</summary>
public sealed record RegistryCatalog(IReadOnlyList<string> Repositories, string? Error);

/// <summary>The tags of a single repository.</summary>
public sealed record RegistryTagList(string Repository, IReadOnlyList<string> Tags, string? Error);
