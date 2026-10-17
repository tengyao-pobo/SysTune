using System.Net;
using System.Net.Http.Headers;
using SysTune.Core.Media;
using SysTune.Core.Spotify;

namespace SysTune.Core.Tests;

public class SpotifyApiTests
{
    private static (SpotifyApiClient Api, FakeHandler Handler, SpotifyConnection Conn, FakeClock Clock) Create(FakeHandler? authHandler = null)
    {
        var clock = new FakeClock();
        var store = new MemoryTokenStore();
        store.Save(new TokenSet("AT", "RT", clock.UtcNow.AddHours(1), "s"));
        var conn = new SpotifyConnection(new SpotifyAuthClient(new HttpClient(authHandler ?? new FakeHandler()), clock), store, clock) { ClientId = "cid" };
        var handler = new FakeHandler();
        return (new SpotifyApiClient(new HttpClient(handler), conn), handler, conn, clock);
    }

    [Fact]
    public async Task GetPlayback_ParsesTrackFixture()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, Fixture.Read("player_track.json"));
        var p = await api.GetPlaybackAsync(CancellationToken.None);
        Assert.NotNull(p);
        Assert.True(p.IsPlaying);
        Assert.Equal(61234, p.ProgressMs);
        Assert.True(p.Shuffle);
        Assert.Equal(RepeatMode.Context, p.Repeat);
        Assert.Equal("Get Lucky", p.Item!.Title);
        Assert.Equal("Daft Punk, Pharrell Williams", p.Item.Subtitle);
        Assert.Equal("Random Access Memories", p.Item.Album);
        Assert.Equal("https://i.scdn.co/image/300", p.Item.ImageUrlLarge);
        Assert.Equal("https://i.scdn.co/image/64", p.Item.ImageUrlSmall);
        Assert.Equal("Living Room Speaker", p.Device!.Name);
        Assert.Equal(42, p.Device.VolumePercent);
        Assert.True(p.Disallows.Resuming);
        Assert.False(p.Disallows.Seeking);

        var req = handler.Requests.Single().Request;
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("AT", req.Headers.Authorization.Parameter);
        Assert.Contains("additional_types=track,episode", req.RequestUri!.Query);
    }

    [Fact]
    public async Task GetPlayback_ParsesEpisodeAndRestrictedDevice()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, Fixture.Read("player_episode.json"));
        var p = await api.GetPlaybackAsync(CancellationToken.None);
        Assert.True(p!.Item!.IsEpisode);
        Assert.Equal("Some Podcast", p.Item.Subtitle);
        Assert.Equal("https://i.scdn.co/image/ep", p.Item.ImageUrlLarge);
        Assert.True(p.Device!.IsRestricted);
        Assert.False(p.Device.SupportsVolume);
    }

    [Fact]
    public async Task GetPlayback_204_MeansNothingPlaying()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.NoContent);
        Assert.Null(await api.GetPlaybackAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unauthorized_RefreshesOnceAndRetries()
    {
        var auth = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"access_token":"NEW","expires_in":3600}""");
        var (api, handler, _, _) = Create(auth);
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"error":{"status":401,"message":"The access token expired"}}""")
               .Enqueue(HttpStatusCode.NoContent);
        Assert.Null(await api.GetPlaybackAsync(CancellationToken.None));
        Assert.Equal("NEW", handler.Requests[1].Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task TooManyRequests_ExposesRetryAfter()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests, null, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17)));
        var ex = await Assert.ThrowsAsync<SpotifyRateLimitException>(() => api.GetPlaybackAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(17), ex.RetryAfter);
    }

    [Fact]
    public async Task Forbidden_PremiumRequired_HasFriendlyMessage()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"error":{"status":403,"message":"Player command failed: Premium required","reason":"PREMIUM_REQUIRED"}}""");
        var ex = await Assert.ThrowsAsync<SpotifyException>(() => api.PauseAsync(null, CancellationToken.None));
        Assert.Equal("PREMIUM_REQUIRED", ex.Reason);
        Assert.Contains("Premium", ex.Message);
    }

    [Fact]
    public async Task NoActiveDevice_404()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"status":404,"message":"Player command failed: No active device found","reason":"NO_ACTIVE_DEVICE"}}""");
        var ex = await Assert.ThrowsAsync<SpotifyException>(() => api.NextAsync(null, CancellationToken.None));
        Assert.Equal("NO_ACTIVE_DEVICE", ex.Reason);
    }

    [Fact]
    public async Task ServerErrorsAndTransportErrors_AreNetworkExceptions()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.BadGateway, "<html>")
               .EnqueueThrow(new HttpRequestException("offline"));
        await Assert.ThrowsAsync<SpotifyNetworkException>(() => api.GetPlaybackAsync(CancellationToken.None));
        await Assert.ThrowsAsync<SpotifyNetworkException>(() => api.GetPlaybackAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Commands_UseCorrectVerbsAndQueries()
    {
        var (api, handler, _, _) = Create();
        for (var i = 0; i < 7; i++) handler.Enqueue(HttpStatusCode.NoContent);
        await api.PlayAsync("dev 1", CancellationToken.None);
        await api.NextAsync(null, CancellationToken.None);
        await api.SeekAsync(-5, null, CancellationToken.None);
        await api.SetVolumeAsync(150, "d", CancellationToken.None);
        await api.SetShuffleAsync(true, null, CancellationToken.None);
        await api.SetRepeatAsync(RepeatMode.Track, null, CancellationToken.None);
        await api.TransferAsync("dev\"x", play: true, CancellationToken.None);

        var r = handler.Requests;
        Assert.Equal((HttpMethod.Put, "/v1/me/player/play?device_id=dev%201"), (r[0].Request.Method, r[0].Request.RequestUri!.PathAndQuery));
        Assert.Equal(HttpMethod.Post, r[1].Request.Method);
        Assert.EndsWith("seek?position_ms=0", r[2].Request.RequestUri!.PathAndQuery);
        Assert.EndsWith("volume?volume_percent=100&device_id=d", r[3].Request.RequestUri!.PathAndQuery);
        Assert.EndsWith("shuffle?state=true", r[4].Request.RequestUri!.PathAndQuery);
        Assert.EndsWith("repeat?state=track", r[5].Request.RequestUri!.PathAndQuery);
        Assert.Equal("{\"device_ids\":[\"dev\\u0022x\"],\"play\":true}", r[6].Body);
        Assert.Equal("application/json", r[6].Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Devices_AreMapped()
    {
        var (api, handler, _, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"devices":[{"id":"1","is_active":false,"name":"Phone","type":"Smartphone","volume_percent":80,"supports_volume":true},{"id":"2","is_active":true,"name":"","type":"Computer"}]}""");
        var devices = await api.GetDevicesAsync(CancellationToken.None);
        Assert.Equal(2, devices.Count);
        Assert.Equal("Phone", devices[0].Name);
        Assert.Equal("未知裝置", devices[1].Name);
        Assert.True(devices[1].IsActive);
    }

    [Fact]
    public void NowPlaying_ExtrapolatesProgressOnlyWhilePlaying()
    {
        var np = new NowPlaying(NowPlayingSource.SpotifyConnect, "k", "t", "a", "al", null, 10_000, 4_000, true,
            TimeSpan.FromSeconds(10), null, null, null, false, RepeatMode.Off, true, true, false);
        Assert.Equal(6_000, np.ProgressAt(TimeSpan.FromSeconds(12)));
        Assert.Equal(10_000, np.ProgressAt(TimeSpan.FromSeconds(60)));
        Assert.Equal(4_000, (np with { IsPlaying = false }).ProgressAt(TimeSpan.FromSeconds(60)));
    }
}
