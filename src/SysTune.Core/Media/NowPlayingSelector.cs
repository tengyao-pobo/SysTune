using SysTune.Core.Spotify;

namespace SysTune.Core.Media;

/// <summary>Decides which source the widget displays.</summary>
public static class NowPlayingSelector
{
    /// <summary>
    /// Priority: Spotify Connect playback (covers every device) → local Windows media session →
    /// last known Connect playback while temporarily offline/rate-limited → nothing.
    /// </summary>
    public static NowPlaying? Select(SpotifyServiceState web, NowPlaying? local, bool useLocal)
    {
        if (web.Status == SpotifyStatus.Connected && web.NowPlaying is not null) return web.NowPlaying;
        if (useLocal && local is not null) return local;
        if (web.Status is SpotifyStatus.Offline or SpotifyStatus.RateLimited && web.NowPlaying is not null) return web.NowPlaying;
        return null;
    }
}
