using System.Net;
using System.Net.Sockets;
using SysTune.Core.Spotify;

namespace SysTune.Core.Tests;

public class SpotifyAuthTests
{
    [Fact]
    public void Pkce_MatchesRfc7636AppendixBVector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Pkce_VerifierUsesUnreservedCharsAndIsRandom()
    {
        var a = Pkce.CreateVerifier();
        var b = Pkce.CreateVerifier();
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9\\-._~]+$", a);
        Assert.Throws<ArgumentOutOfRangeException>(() => Pkce.CreateVerifier(10));
        Assert.DoesNotContain('=', Pkce.CreateState());
    }

    [Fact]
    public void AuthorizeUri_ContainsPkceParametersAndNoSecret()
    {
        var uri = SpotifyAuthClient.BuildAuthorizeUri("client123", "http://127.0.0.1:47821/callback", "chal", "st&ate");
        var q = uri.Query;
        Assert.StartsWith("https://accounts.spotify.com/authorize?", uri.AbsoluteUri);
        Assert.Contains("client_id=client123", q);
        Assert.Contains("code_challenge_method=S256", q);
        Assert.Contains("code_challenge=chal", q);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A47821%2Fcallback", q);
        Assert.Contains("state=st%26ate", q);
        Assert.Contains("user-modify-playback-state", Uri.UnescapeDataString(q));
        Assert.DoesNotContain("secret", q);
    }

    [Fact]
    public async Task Exchange_SendsVerifierAndParsesTokens()
    {
        var handler = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"access_token":"AT","token_type":"Bearer","scope":"s","expires_in":3600,"refresh_token":"RT"}""");
        var clock = new FakeClock();
        var auth = new SpotifyAuthClient(new HttpClient(handler), clock);
        var t = await auth.ExchangeCodeAsync("cid", "the-code", "http://127.0.0.1:1/callback", "verifier", CancellationToken.None);
        Assert.Equal("AT", t.AccessToken);
        Assert.Equal("RT", t.RefreshToken);
        Assert.Equal(clock.UtcNow.AddHours(1), t.ExpiresAt);
        var body = handler.Requests.Single().Body!;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code_verifier=verifier", body);
        Assert.Contains("client_id=cid", body);
        Assert.DoesNotContain("client_secret", body);
        Assert.DoesNotContain("AT", t.ToString()); // tokens never printed
    }

    [Fact]
    public async Task Refresh_KeepsOldRefreshTokenWhenNotRotated()
    {
        var handler = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"access_token":"AT2","expires_in":3600}""");
        var auth = new SpotifyAuthClient(new HttpClient(handler), new FakeClock());
        var t = await auth.RefreshAsync("cid", "OLD", CancellationToken.None);
        Assert.Equal("OLD", t.RefreshToken);
        Assert.Contains("grant_type=refresh_token", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Refresh_InvalidGrant_ThrowsAuthException()
    {
        var handler = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Refresh token revoked"}""");
        var auth = new SpotifyAuthClient(new HttpClient(handler), new FakeClock());
        var ex = await Assert.ThrowsAsync<SpotifyAuthException>(() => auth.RefreshAsync("cid", "RT", CancellationToken.None));
        Assert.Equal("invalid_grant", ex.Reason);
    }

    [Fact]
    public async Task TokenEndpoint_NetworkFailure_IsTransient()
    {
        var handler = new FakeHandler().EnqueueThrow(new HttpRequestException("dns"));
        var auth = new SpotifyAuthClient(new HttpClient(handler), new FakeClock());
        await Assert.ThrowsAsync<SpotifyNetworkException>(() => auth.RefreshAsync("cid", "RT", CancellationToken.None));
    }

    [Fact]
    public void CallbackParser_HandlesCodeErrorAndOtherPaths()
    {
        Assert.True(LoopbackCallbackServer.TryParse("/callback?code=a%2Fb&state=xyz", "/callback", out var ok));
        Assert.Equal("a/b", ok.Code);
        Assert.Equal("xyz", ok.State);
        Assert.True(LoopbackCallbackServer.TryParse("/callback?error=access_denied&state=s", "/callback", out var denied));
        Assert.Equal("access_denied", denied.Error);
        Assert.False(LoopbackCallbackServer.TryParse("/favicon.ico", "/callback", out _));
        Assert.False(LoopbackCallbackServer.TryParse("/callback", "/callback", out _));
    }

    [Fact]
    public async Task LoopbackServer_ReceivesRealHttpRedirect()
    {
        using var server = new LoopbackCallbackServer(0);
        var wait = server.WaitForCallbackAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var notFound = await http.GetAsync($"http://127.0.0.1:{server.Port}/favicon.ico");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        var page = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/callback?code=CODE&state=STATE");
        Assert.Contains("已連結 Spotify", page);
        var cb = await wait;
        Assert.Equal("CODE", cb.Code);
        Assert.Equal("STATE", cb.State);
    }

    [Fact]
    public void LoopbackServer_OnlyBindsLoopback_AndPortConflictIsReported()
    {
        using var server = new LoopbackCallbackServer(0);
        Assert.Throws<SocketException>(() => new LoopbackCallbackServer(server.Port));
    }

    [Fact]
    public async Task Connection_RefreshesNearExpiry_AndDisconnectsOnRevocation()
    {
        var clock = new FakeClock();
        var store = new MemoryTokenStore();
        store.Save(new TokenSet("AT", "RT", clock.UtcNow.AddMinutes(30), "s"));
        var handler = new FakeHandler()
            .Enqueue(HttpStatusCode.OK, """{"access_token":"AT2","expires_in":3600,"refresh_token":"RT2"}""")
            .Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        using var conn = new SpotifyConnection(new SpotifyAuthClient(new HttpClient(handler), clock), store, clock) { ClientId = "cid" };

        Assert.Equal("AT", await conn.GetAccessTokenAsync(false, CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(29)); // within the 90 s skew
        Assert.Equal("AT2", await conn.GetAccessTokenAsync(false, CancellationToken.None));
        Assert.Equal("RT2", store.Load()!.RefreshToken);

        var changed = 0;
        conn.StateChanged += (_, _) => changed++;
        await Assert.ThrowsAsync<SpotifyAuthException>(() => conn.GetAccessTokenAsync(true, CancellationToken.None));
        Assert.False(conn.IsConnected);
        Assert.Null(store.Load());
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Connection_WithoutClientId_FailsFast()
    {
        using var conn = new SpotifyConnection(new SpotifyAuthClient(new HttpClient(new FakeHandler())), new MemoryTokenStore());
        var ex = await Assert.ThrowsAsync<SpotifyAuthException>(() => conn.ConnectAsync(0, _ => { }, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal("missing_client_id", ex.Reason);
    }

    [Fact]
    public async Task Connection_FullPkceLoginOverLoopback()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var handler = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"access_token":"AT","expires_in":3600,"refresh_token":"RT","scope":"x"}""");
        var store = new MemoryTokenStore();
        using var conn = new SpotifyConnection(new SpotifyAuthClient(new HttpClient(handler)), store) { ClientId = "cid" };
        using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        await conn.ConnectAsync(port, uri =>
        {
            // Simulate the user approving in the browser: Spotify redirects back with code + the same state.
            var state = System.Web.HttpUtility.ParseQueryString(uri.Query)["state"];
            _ = browser.GetStringAsync($"http://127.0.0.1:{port}/callback?code=CODE&state={Uri.EscapeDataString(state!)}");
        }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(conn.IsConnected);
        Assert.Equal("RT", store.Load()!.RefreshToken);
        Assert.Contains("code=CODE", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Connection_RejectsStateMismatch()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var conn = new SpotifyConnection(new SpotifyAuthClient(new HttpClient(new FakeHandler())), new MemoryTokenStore()) { ClientId = "cid" };
        using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var ex = await Assert.ThrowsAsync<SpotifyAuthException>(() => conn.ConnectAsync(port,
            uri => _ = browser.GetStringAsync($"http://127.0.0.1:{port}/callback?code=CODE&state=forged"),
            TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal("state_mismatch", ex.Reason);
        Assert.False(conn.IsConnected);
    }
}
