namespace SysTune.Core.Media;

public enum NowPlayingSource
{
    None,

    /// <summary>Spotify Web API (works across all Spotify Connect devices).</summary>
    SpotifyConnect,

    /// <summary>Windows media session of the local Spotify desktop app (no login required).</summary>
    LocalSession,
}

/// <summary>Source-agnostic "now playing" information shown by the widget.</summary>
/// <param name="CapturedAt">Monotonic time at which <paramref name="ProgressMs"/> was valid.</param>
public sealed record NowPlaying(
    NowPlayingSource Source,
    string ItemKey,
    string Title,
    string Artist,
    string Album,
    string? ArtworkUrl,
    int DurationMs,
    int ProgressMs,
    bool IsPlaying,
    TimeSpan CapturedAt,
    string? DeviceName,
    string? DeviceType,
    int? VolumePercent,
    bool Shuffle,
    Spotify.RepeatMode Repeat,
    bool CanSeek,
    bool CanControl,
    bool IsAd)
{
    /// <summary>Extrapolated playback position, so the progress bar moves smoothly between polls.</summary>
    public int ProgressAt(TimeSpan now)
    {
        if (!IsPlaying) return Math.Clamp(ProgressMs, 0, Math.Max(DurationMs, 0));
        var p = ProgressMs + (long)(now - CapturedAt).TotalMilliseconds;
        return (int)Math.Clamp(p, 0, DurationMs > 0 ? DurationMs : int.MaxValue);
    }
}
