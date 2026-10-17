using System.Net;

namespace SysTune.Core.Spotify;

/// <summary>Base class for all Spotify failures surfaced to the app.</summary>
public class SpotifyException : Exception
{
    public SpotifyException()
    {
    }

    public SpotifyException(string message) : base(message)
    {
    }

    public SpotifyException(string message, Exception inner) : base(message, inner)
    {
    }

    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>Spotify "reason" field (e.g. PREMIUM_REQUIRED, NO_ACTIVE_DEVICE) when present.</summary>
    public string? Reason { get; init; }
}

/// <summary>The refresh token was revoked/expired, or the client ID is invalid: the user must log in again.</summary>
public sealed class SpotifyAuthException : SpotifyException
{
    public SpotifyAuthException()
    {
    }

    public SpotifyAuthException(string message) : base(message)
    {
    }

    public SpotifyAuthException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>HTTP 429. <see cref="RetryAfter"/> comes from the Retry-After header.</summary>
public sealed class SpotifyRateLimitException : SpotifyException
{
    public SpotifyRateLimitException()
    {
    }

    public SpotifyRateLimitException(string message) : base(message)
    {
    }

    public SpotifyRateLimitException(string message, Exception inner) : base(message, inner)
    {
    }

    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Transport failure (offline, DNS, timeout, 5xx). Always transient.</summary>
public sealed class SpotifyNetworkException : SpotifyException
{
    public SpotifyNetworkException()
    {
    }

    public SpotifyNetworkException(string message) : base(message)
    {
    }

    public SpotifyNetworkException(string message, Exception inner) : base(message, inner)
    {
    }
}
