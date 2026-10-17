using SysTune.Core.Common;
using SysTune.Core.Logging;
using SysTune.Core.Media;

namespace SysTune.Core.Spotify;

public enum SpotifyStatus
{
    /// <summary>No Client ID configured.</summary>
    NotConfigured,

    /// <summary>Configured but not logged in (or the login was revoked).</summary>
    Disconnected,

    Connecting,

    Connected,

    /// <summary>Network unavailable; retrying with backoff.</summary>
    Offline,

    RateLimited,

    /// <summary>Unexpected API error (e.g. user not allow-listed in a development-mode app).</summary>
    Error,
}

public sealed record SpotifyServiceState(SpotifyStatus Status, NowPlaying? NowPlaying, string? Message);

/// <summary>
/// Background polling loop for the Spotify Web API. Adapts its interval to the playback state,
/// honours Retry-After on 429, backs off exponentially when offline, and pauses completely while the
/// widget is hidden. Failures never propagate to the caller – they become <see cref="SpotifyServiceState"/>.
/// </summary>
public sealed class SpotifyPlaybackService : IAsyncDisposable
{
    private readonly SpotifyConnection _connection;
    private readonly SpotifyApiClient _api;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly AsyncSignal _wake = new();
    private readonly Backoff _backoff = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60));
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile bool _active = true;
    private volatile bool _connecting;
    private SpotifyServiceState _state = new(SpotifyStatus.Disconnected, null, null);
    private PlaybackState? _lastPlayback;

    public SpotifyPlaybackService(SpotifyConnection connection, SpotifyApiClient api, IClock? clock = null, ILog? log = null)
    {
        _connection = connection;
        _api = api;
        _clock = clock ?? SystemClock.Instance;
        _log = log ?? NullLog.Instance;
        _connection.StateChanged += (_, _) => _wake.Set();
    }

    /// <summary>Raised on a background thread whenever <see cref="State"/> changes.</summary>
    public event EventHandler<SpotifyServiceState>? StateChanged;

    public SpotifyServiceState State => Volatile.Read(ref _state);

    public SpotifyConnection Connection => _connection;

    public SpotifyApiClient Api => _api;

    public string? ActiveDeviceId => _lastPlayback?.Device?.Id;

    /// <summary>When false (widget hidden) polling stops until re-activated.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            if (value) _wake.Set();
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Requests an immediate refresh (e.g. after a playback command).</summary>
    public void Nudge() => _wake.Set();

    public async Task ConnectAsync(int port, Action<Uri> openBrowser, CancellationToken ct)
    {
        _connecting = true;
        Publish(new SpotifyServiceState(SpotifyStatus.Connecting, null, "請在瀏覽器中完成 Spotify 授權…"));
        try
        {
            await _connection.ConnectAsync(port, openBrowser, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Publish(new SpotifyServiceState(BaseStatus(), null, ex is SpotifyException ? ex.Message : "連結 Spotify 時發生錯誤"));
            if (ex is not SpotifyException and not OperationCanceledException) _log.Error("Spotify connect failed", ex);
            throw;
        }
        finally
        {
            _connecting = false;
            _wake.Set();
        }
    }

    public void Disconnect()
    {
        _connection.Disconnect();
        _lastPlayback = null;
        Publish(new SpotifyServiceState(BaseStatus(), null, null));
    }

    /// <summary>
    /// Runs a playback command. Returns null on success or a user-facing error message.
    /// <paramref name="optimistic"/> is applied to the displayed state immediately for snappy UI.
    /// </summary>
    public async Task<string?> ExecuteAsync(Func<SpotifyApiClient, CancellationToken, Task> command, Func<PlaybackState, PlaybackState>? optimistic = null)
    {
        if (optimistic is not null && _lastPlayback is { } current)
        {
            var next = optimistic(current);
            _lastPlayback = next;
            Publish(new SpotifyServiceState(SpotifyStatus.Connected, ToNowPlaying(next), null));
        }
        try
        {
            await command(_api, _cts.Token).ConfigureAwait(false);
            return null;
        }
        catch (SpotifyException ex)
        {
            _log.Info($"Spotify command failed: {ex.StatusCode} {ex.Reason} {ex.Message}");
            return ex.Message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // Give Spotify a moment to apply the change before re-reading state.
            _ = Task.Delay(400).ContinueWith(_ => _wake.Set(), TaskScheduler.Default);
        }
    }

    private SpotifyStatus BaseStatus()
        => string.IsNullOrWhiteSpace(_connection.ClientId) && !_connection.IsConnected ? SpotifyStatus.NotConfigured
            : _connection.IsConnected ? SpotifyStatus.Connected : SpotifyStatus.Disconnected;

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("Unexpected error in Spotify loop", ex);
                Publish(new SpotifyServiceState(SpotifyStatus.Error, null, "Spotify 發生未預期錯誤，稍後重試"));
                wait = TimeSpan.FromSeconds(30);
            }

            try
            {
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One polling step; returns how long to wait before the next one.</summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        if (_connecting) return Timeout.InfiniteTimeSpan;
        if (!_connection.IsConnected)
        {
            _lastPlayback = null;
            if (State.Status is not (SpotifyStatus.Disconnected or SpotifyStatus.NotConfigured) || State.NowPlaying is not null)
            {
                Publish(new SpotifyServiceState(BaseStatus(), null, State.Message));
            }
            else if (State.Status != BaseStatus())
            {
                Publish(State with { Status = BaseStatus() });
            }
            return Timeout.InfiniteTimeSpan;
        }
        if (!_active) return Timeout.InfiniteTimeSpan;

        try
        {
            var playback = await _api.GetPlaybackAsync(ct).ConfigureAwait(false);
            _backoff.Reset();
            _lastPlayback = playback;
            var np = playback is null ? null : ToNowPlaying(playback);
            Publish(new SpotifyServiceState(SpotifyStatus.Connected, np, null));
            return NextInterval(playback);
        }
        catch (SpotifyRateLimitException ex)
        {
            Publish(new SpotifyServiceState(SpotifyStatus.RateLimited, State.NowPlaying, ex.Message));
            return ex.RetryAfter;
        }
        catch (SpotifyNetworkException ex)
        {
            var delay = _backoff.Next();
            if (_backoff.Attempt == 1) _log.Info($"Spotify offline: {ex.Message}");
            Publish(new SpotifyServiceState(SpotifyStatus.Offline, State.NowPlaying, "網路無法連線，稍後自動重試"));
            return delay;
        }
        catch (SpotifyAuthException ex)
        {
            _connection.Disconnect();
            Publish(new SpotifyServiceState(BaseStatus(), null, ex.Message));
            return Timeout.InfiniteTimeSpan;
        }
        catch (SpotifyException ex)
        {
            _log.Warn($"Spotify API error {(int?)ex.StatusCode} {ex.Reason}: {ex.Message}");
            Publish(new SpotifyServiceState(SpotifyStatus.Error, null, ex.Message));
            return TimeSpan.FromSeconds(30);
        }
    }

    /// <summary>Adaptive poll interval. Wakes right after the current track should end.</summary>
    internal static TimeSpan NextInterval(PlaybackState? playback)
    {
        if (playback?.Item is null) return TimeSpan.FromSeconds(8);
        if (!playback.IsPlaying) return TimeSpan.FromSeconds(5);
        var remaining = playback.Item.DurationMs - playback.ProgressMs;
        var interval = TimeSpan.FromSeconds(3);
        if (remaining > 0 && remaining + 500 < interval.TotalMilliseconds)
        {
            interval = TimeSpan.FromMilliseconds(Math.Max(700, remaining + 500));
        }
        return interval;
    }

    internal NowPlaying ToNowPlaying(PlaybackState p)
    {
        var item = p.Item;
        var isAd = string.Equals(p.PlayingType, "ad", StringComparison.Ordinal);
        var restricted = p.Device?.IsRestricted ?? false;
        return new NowPlaying(
            NowPlayingSource.SpotifyConnect,
            item?.Id ?? (isAd ? "ad" : ""),
            item?.Title ?? (isAd ? "廣告" : "正在播放"),
            item?.Subtitle ?? "",
            item?.Album ?? "",
            item?.ImageUrlLarge,
            item?.DurationMs ?? 0,
            p.ProgressMs,
            p.IsPlaying,
            _clock.Elapsed,
            p.Device?.Name,
            p.Device?.Type,
            p.Device?.SupportsVolume == true ? p.Device.VolumePercent : null,
            p.Shuffle,
            p.Repeat,
            CanSeek: !restricted && !p.Disallows.Seeking && item is not null,
            CanControl: !restricted,
            isAd);
    }

    private void Publish(SpotifyServiceState state)
    {
        Volatile.Write(ref _state, state);
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _log.Error("Spotify state handler threw", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
    }
}
