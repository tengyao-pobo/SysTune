using SysTune.Core.Media;
using SysTune.Core.Spotify;

namespace SysTune.Core.Tests;

public class NowPlayingSelectorTests
{
    private static NowPlaying Np(NowPlayingSource s) => new(s, "k", "t", "a", "al", null, 1, 0, true, TimeSpan.Zero, null, null, null, false, RepeatMode.Off, true, true, false);

    [Fact]
    public void PrefersConnectThenLocal()
    {
        var web = Np(NowPlayingSource.SpotifyConnect);
        var local = Np(NowPlayingSource.LocalSession);
        Assert.Same(web, NowPlayingSelector.Select(new SpotifyServiceState(SpotifyStatus.Connected, web, null), local, true));
        Assert.Same(local, NowPlayingSelector.Select(new SpotifyServiceState(SpotifyStatus.Connected, null, null), local, true));
        Assert.Same(local, NowPlayingSelector.Select(new SpotifyServiceState(SpotifyStatus.Disconnected, null, null), local, true));
        Assert.Null(NowPlayingSelector.Select(new SpotifyServiceState(SpotifyStatus.Disconnected, null, null), local, false));
    }

    [Fact]
    public void KeepsLastConnectStateWhileOffline()
    {
        var web = Np(NowPlayingSource.SpotifyConnect);
        Assert.Same(web, NowPlayingSelector.Select(new SpotifyServiceState(SpotifyStatus.Offline, web, null), null, true));
        Assert.Null(NowPlayingSelector.Select(new SpotifyServiceState(SpotifyStatus.Error, web, null), null, true));
    }
}
