namespace SysTune.Core.Spotify;

/// <summary>OAuth tokens for the Spotify Web API. Treat as a secret; never log.</summary>
public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Scope)
{
    /// <summary>True when the access token expires within <paramref name="skew"/>.</summary>
    public bool IsExpiring(DateTimeOffset now, TimeSpan skew) => now + skew >= ExpiresAt;

    public override string ToString() => $"TokenSet(expires {ExpiresAt:O}, scope '{Scope}')";
}

/// <summary>Persists tokens securely (DPAPI on Windows).</summary>
public interface ITokenStore
{
    TokenSet? Load();

    void Save(TokenSet tokens);

    void Clear();
}

/// <summary>Volatile store, used in tests or when secure storage is unavailable.</summary>
public sealed class MemoryTokenStore : ITokenStore
{
    private TokenSet? _tokens;

    public TokenSet? Load() => _tokens;

    public void Save(TokenSet tokens) => _tokens = tokens;

    public void Clear() => _tokens = null;
}
