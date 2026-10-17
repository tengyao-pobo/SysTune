using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SysTune.Core.Spotify;

namespace SysTune.App.Services;

/// <summary>
/// Stores Spotify tokens encrypted with Windows DPAPI (CurrentUser scope + app-specific entropy),
/// so only the same Windows user on the same machine can decrypt them. Stored under %LOCALAPPDATA%
/// (non-roaming) because DPAPI blobs are machine-bound.
/// </summary>
internal sealed class DpapiTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = "SysTune.Spotify.Tokens.v1"u8.ToArray();
    private readonly string _path;

    public DpapiTokenStore(string path) => _path = path;

    public TokenSet? Load()
    {
        if (!File.Exists(_path)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var doc = JsonDocument.Parse(plain);
            var root = doc.RootElement;
            return new TokenSet(
                root.GetProperty("a").GetString()!,
                root.GetProperty("r").GetString()!,
                root.GetProperty("e").GetDateTimeOffset(),
                root.TryGetProperty("s", out var s) ? s.GetString() ?? "" : "");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Save(TokenSet tokens)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("a", tokens.AccessToken);
            w.WriteString("r", tokens.RefreshToken);
            w.WriteString("e", tokens.ExpiresAt);
            w.WriteString("s", tokens.Scope);
            w.WriteEndObject();
        }
        var plain = ms.ToArray();
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
