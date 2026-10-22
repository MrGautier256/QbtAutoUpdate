using System.Security.Cryptography;
using System.Text;

namespace QbtAutoUpdate.Platform;

/// <summary>
/// Encrypts WebUI secrets for the current Windows user (DPAPI, CurrentUser scope): user.json can
/// be read by nobody else, and is useless on another machine or account.
/// </summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = "QbtAutoUpdate.WebUI.v1"u8.ToArray();

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Returns null when the value is damaged or was protected by another user or machine.</summary>
    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
