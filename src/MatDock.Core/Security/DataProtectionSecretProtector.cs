using Microsoft.AspNetCore.DataProtection;

namespace MatDock.Core.Security;

/// <summary>Data-Protection-backed implementation of <see cref="ISecretProtector"/>.</summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("MatDock.Secrets.v1");
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        return _protector.Unprotect(protectedValue);
    }

    public string? ProtectNullable(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? null : Protect(plaintext);

    public string? UnprotectNullable(string? protectedValue)
        => string.IsNullOrEmpty(protectedValue) ? null : Unprotect(protectedValue);
}
