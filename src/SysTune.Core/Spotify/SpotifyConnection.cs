using SysTune.Core.Common;
using SysTune.Core.Logging;

namespace SysTune.Core.Spotify;

/// <summary>
/// Owns the Spotify login state: performs the interactive PKCE login, keeps tokens fresh, and
/// persists them via <see cref="ITokenStore"/>. Thread-safe.
/// </summary>
public sealed class SpotifyConnection : IDisposable
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(90);
    private readonly SpotifyAuthClient _auth;
    private readonly ITokenStore _store;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TokenSet? _tokens;
    private string _clientId = "";

    public SpotifyConnection(SpotifyAuthClient auth, ITokenStore store, IClock? clock = null, ILog? log = null)
    {
        _auth = auth;
        _store = store;
        _clock = clock ?? SystemClock.Instance;
        _log = log ?? NullLog.Instance;
        try
        {
            _tokens = _store.Load();
        }
        catch (Exception ex)
        {
            _log.Warn("Could not load stored Spotify tokens", ex);
            _tokens = null;
        }
    }

    /// <summary>Raised (on an arbitrary thread) when the connection is established or lost.</summary>
    public event EventHandler? StateChanged;

    public bool IsConnected => Volatile.Read(ref _tokens) is not null;

    public string ClientId
    {
        get => _clientId;
        set => _clientId = (value ?? "").Trim();
    }

    /// <summary>
    /// Runs the interactive login: starts the loopback listener, asks the caller to open the browser
    /// and waits (up to <paramref name="timeout"/>) for Spotify to redirect back.
    /// </summary>
    public async Task ConnectAsync(int callbackPort, Action<Uri> openBrowser, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            throw new SpotifyAuthException("尚未設定 Spotify Client ID") { Reason = "missing_client_id" };
        }

        var redirectUri = $"http://127.0.0.1:{callbackPort}/callback";
        var verifier = Pkce.CreateVerifier();
        var state = Pkce.CreateState();

        LoopbackCallbackServer server;
        try
        {
            server = new LoopbackCallbackServer(callbackPort);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new SpotifyException($"無法監聽 127.0.0.1:{callbackPort}（連接埠可能被占用），請在設定中更換回呼連接埠", ex) { Reason = "port_in_use" };
        }

        using (server)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            openBrowser(SpotifyAuthClient.BuildAuthorizeUri(_clientId, redirectUri, Pkce.CreateChallenge(verifier), state));

            AuthorizationCallback callback;
            try
            {
                callback = await server.WaitForCallbackAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SpotifyAuthException("等待 Spotify 授權逾時") { Reason = "timeout" };
            }

            if (callback.Error is not null)
            {
                throw new SpotifyAuthException(callback.Error == "access_denied" ? "已取消 Spotify 授權" : $"Spotify 授權失敗：{callback.Error}") { Reason = callback.Error };
            }
            if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            {
                throw new SpotifyAuthException("授權狀態驗證失敗（state 不符），已中止") { Reason = "state_mismatch" };
            }

            var tokens = await _auth.ExchangeCodeAsync(_clientId, callback.Code!, redirectUri, verifier, ct).ConfigureAwait(false);
            SetTokens(tokens);
            _log.Info("Spotify connected; " + SpotifyAuthClient.Describe(tokens));
        }
    }

    /// <summary>Returns a valid access token, refreshing it when it is about to expire.</summary>
    /// <exception cref="SpotifyAuthException">Not connected or refresh token revoked.</exception>
    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var tokens = Volatile.Read(ref _tokens) ?? throw new SpotifyAuthException("尚未連結 Spotify") { Reason = "not_connected" };
        if (!forceRefresh && !tokens.IsExpiring(_clock.UtcNow, RefreshSkew)) return tokens.AccessToken;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _tokens) ?? throw new SpotifyAuthException("尚未連結 Spotify") { Reason = "not_connected" };
            // Another caller may have refreshed while we waited.
            if (!ReferenceEquals(current, tokens) && !current.IsExpiring(_clock.UtcNow, RefreshSkew)) return current.AccessToken;
            if (string.IsNullOrWhiteSpace(_clientId))
            {
                throw new SpotifyAuthException("尚未設定 Spotify Client ID") { Reason = "missing_client_id" };
            }
            try
            {
                var refreshed = await _auth.RefreshAsync(_clientId, current.RefreshToken, ct).ConfigureAwait(false);
                SetTokens(refreshed);
                _log.Debug("Spotify token refreshed; " + SpotifyAuthClient.Describe(refreshed));
                return refreshed.AccessToken;
            }
            catch (SpotifyAuthException ex)
            {
                _log.Warn($"Spotify refresh rejected ({ex.Reason}); disconnecting");
                Disconnect();
                throw;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Disconnect()
    {
        var had = Interlocked.Exchange(ref _tokens, null) is not null;
        try
        {
            _store.Clear();
        }
        catch (Exception ex)
        {
            _log.Warn("Could not clear stored Spotify tokens", ex);
        }
        if (had) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetTokens(TokenSet tokens)
    {
        var wasConnected = Interlocked.Exchange(ref _tokens, tokens) is not null;
        try
        {
            _store.Save(tokens);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not persist Spotify tokens; login will last until exit", ex);
        }
        if (!wasConnected) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _refreshGate.Dispose();
}
