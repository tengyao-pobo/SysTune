using System.Security.Cryptography;
using System.Text;

namespace SysTune.Core.Spotify;

/// <summary>RFC 7636 Proof Key for Code Exchange helpers.</summary>
public static class Pkce
{
    private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>Creates a cryptographically random code verifier (43-128 chars).</summary>
    public static string CreateVerifier(int length = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 43);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 128);
        return RandomNumberGenerator.GetString(Unreserved, length);
    }

    /// <summary>S256 code challenge: BASE64URL(SHA256(ASCII(verifier))).</summary>
    public static string CreateChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    /// <summary>Random opaque value for the OAuth <c>state</c> parameter (CSRF protection).</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    internal static string Base64Url(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
