using MatDock.Core.Entities;

namespace MatDock.Core.Auth;

/// <summary>Result of a local username/password authentication attempt.</summary>
public sealed class AuthenticationResult
{
    public bool Succeeded { get; private init; }

    public User? User { get; private init; }

    public string? Error { get; private init; }

    public static AuthenticationResult Success(User user) => new() { Succeeded = true, User = user };

    public static AuthenticationResult Failure(string error) => new() { Succeeded = false, Error = error };
}
