using MatDock.Core.Entities;

namespace MatDock.Core.Users;

/// <summary>A person signed in by MatIdentity, as <see cref="UserService.SignInExternalAsync"/> gets it.</summary>
/// <param name="Subject">The claim <c>sub</c>: the stable id of the person.</param>
/// <param name="Email">The claim <c>email</c>.</param>
/// <param name="Name">The claim <c>name</c>.</param>
/// <param name="PreferredUserName">The claim <c>preferred_username</c>.</param>
/// <param name="Role">The claim <c>role</c> (<c>admin</c> or <c>user</c>) when MatIdentity decides the role, else null.</param>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>
/// The outcome of <see cref="UserService.SignInExternalAsync"/>: the user to sign in, the id of the account that matches by its user name and has
/// to be proven by its password before it is linked (<see cref="UserService.LinkExternalAsync"/>), or the reason it is refused.
/// </summary>
public sealed record ExternalSignIn(User? User, long? LinkUserId, string? Error)
{
    public bool Refused => Error is not null;

    public static ExternalSignIn SignedIn(User user) => new(user, null, null);

    public static ExternalSignIn LinkRequired(long userId) => new(null, userId, null);

    public static ExternalSignIn Refuse(string error) => new(null, null, error);
}
