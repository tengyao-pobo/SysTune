using System.Net;
using SysTune.Core.Spotify;

namespace SysTune.Core.Tests;

public class SpotifyServiceTests
{
    private static (SpotifyPlaybackService Svc, FakeHandler Api, SpotifyConnection Conn, List<SpotifyServiceState> States) Create(bool connected = true, FakeHandler? auth = null)
    {
        var clock = new FakeClock();
        var store = new MemoryTokenStore();
        if (connected) store.Save(new TokenSet("AT", "RT", clock.UtcNow.AddHours(1), "s"));
        var conn = new SpotifyConnection(new SpotifyAuthClient(new HttpClient(auth ?? new FakeHandler()), clock), store, clock) { ClientId = "cid" };
        var apiHandler = new FakeHandler();
        var svc = new SpotifyPlaybackService(conn, new SpotifyApiClient(new HttpClient(apiHandler), conn), clock);
        var states = new List<SpotifyServiceState>();
        svc.StateChanged += (_, s) => states.Add(s);
        return (svc, apiHandler, conn, states);
    }

    [Fact]
    public async Task Tick_Connected_PublishesNowPlaying()
    {
        var (svc, api, _, states) = Create();
        api.Enqueue(HttpStatusCode.OK, Fixture.Read("player_track.json"));
        var wait = await svc.TickAsync(CancellationToken.None);
        var s = states.Last();
        Assert.Equal(SpotifyStatus.Connected, s.Status);
        Assert.Equal("Get Lucky", s.NowPlaying!.Title);
        Assert.Equal("Living Room Speaker", s.NowPlaying.DeviceName);
        Assert.Equal(42, s.NowPlaying.VolumePercent);
        Assert.Equal(TimeSpan.FromSeconds(3), wait);
    }

    [Fact]
    public async Task Tick_NotConnected_WaitsIndefinitely()
    {
        var (svc, _, _, states) = Create(connected: false);
        Assert.Equal(Timeout.InfiniteTimeSpan, await svc.TickAsync(CancellationToken.None));
        Assert.Equal(SpotifyStatus.Disconnected, svc.State.Status);
    }

    [Fact]
    public async Task Tick_Offline_BacksOffAndKeepsLastTrack()
    {
        var (svc, api, _, _) = Create();
        api.Enqueue(HttpStatusCode.OK, Fixture.Read("player_track.json"))
           .EnqueueThrow(new HttpRequestException("offline"))
           .EnqueueThrow(new HttpRequestException("offline"));
        await svc.TickAsync(CancellationToken.None);
        var w1 = await svc.TickAsync(CancellationToken.None);
        var w2 = await svc.TickAsync(CancellationToken.None);
        Assert.Equal(SpotifyStatus.Offline, svc.State.Status);
        Assert.NotNull(svc.State.NowPlaying);
        Assert.True(w2 > w1);
    }

    [Fact]
    public async Task Tick_RateLimited_UsesRetryAfter()
    {
        var (svc, api, _, _) = Create();
        api.Enqueue(HttpStatusCode.TooManyRequests, null, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));
        Assert.Equal(TimeSpan.FromSeconds(42), await svc.TickAsync(CancellationToken.None));
        Assert.Equal(SpotifyStatus.RateLimited, svc.State.Status);
    }

    [Fact]
    public async Task Tick_RevokedLogin_Disconnects()
    {
        var auth = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var (svc, api, conn, _) = Create(auth: auth);
        api.Enqueue(HttpStatusCode.Unauthorized, """{"error":{"status":401,"message":"revoked"}}""");
        Assert.Equal(Timeout.InfiniteTimeSpan, await svc.TickAsync(CancellationToken.None));
        Assert.False(conn.IsConnected);
        Assert.Equal(SpotifyStatus.Disconnected, svc.State.Status);
        Assert.NotNull(svc.State.Message);
    }

    [Fact]
    public async Task Tick_Inactive_DoesNotPoll()
    {
        var (svc, api, _, _) = Create();
        svc.IsActive = false;
        Assert.Equal(Timeout.InfiniteTimeSpan, await svc.TickAsync(CancellationToken.None));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Execute_AppliesOptimisticStateAndReportsErrors()
    {
        var (svc, api, _, states) = Create();
        api.Enqueue(HttpStatusCode.OK, Fixture.Read("player_track.json"));
        await svc.TickAsync(CancellationToken.None);
        api.Enqueue(HttpStatusCode.Forbidden, """{"error":{"status":403,"message":"Premium required","reason":"PREMIUM_REQUIRED"}}""");
        var error = await svc.ExecuteAsync((a, ct) => a.PauseAsync(null, ct), p => p with { IsPlaying = false });
        Assert.False(states.Last().NowPlaying!.IsPlaying); // optimistic update was published
        Assert.Contains("Premium", error);
    }

    [Theory]
    [InlineData(false, 0, 100_000, 5000)]
    [InlineData(true, 0, 100_000, 3000)]
    [InlineData(true, 99_000, 100_000, 1500)]
    [InlineData(true, 99_990, 100_000, 700)]
    public void NextInterval_AdaptsToPlayback(bool playing, int progress, int duration, int expectedMs)
    {
        var item = new SpotifyItem("i", "u", "t", "s", "a", null, null, duration, false);
        var p = new PlaybackState(null, item, playing, progress, false, RepeatMode.Off, "track", PlaybackDisallows.None);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), SpotifyPlaybackService.NextInterval(p));
        Assert.Equal(TimeSpan.FromSeconds(8), SpotifyPlaybackService.NextInterval(null));
    }

    [Fact]
    public async Task Loop_StartsAndStopsCleanly()
    {
        var (svc, api, _, states) = Create();
        api.Enqueue(HttpStatusCode.NoContent);
        svc.Start();
        for (var i = 0; i < 50 && states.Count == 0; i++) await Task.Delay(20);
        await svc.DisposeAsync();
        Assert.Equal(SpotifyStatus.Connected, states.Last().Status);
        Assert.Null(states.Last().NowPlaying);
    }
}

public class SpotifyConnectSupersedeTests
{
    [Fact]
    public async Task SecondConnect_CancelsFirstAndReusesPort()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        var auth = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"access_token":"AT","expires_in":3600,"refresh_token":"RT"}""");
        var conn = new SpotifyConnection(new SpotifyAuthClient(new HttpClient(auth)), new MemoryTokenStore()) { ClientId = "cid" };
        await using var svc = new SpotifyPlaybackService(conn, new SpotifyApiClient(new HttpClient(new FakeHandler()), conn));
        using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        var first = svc.ConnectAsync(port, _ => { }, CancellationToken.None);
        await Task.Delay(100);
        var second = svc.ConnectAsync(port, uri =>
        {
            var state = System.Web.HttpUtility.ParseQueryString(uri.Query)["state"];
            _ = browser.GetStringAsync($"http://127.0.0.1:{port}/callback?code=C&state={Uri.EscapeDataString(state!)}");
        }, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.True(conn.IsConnected);
    }
}
