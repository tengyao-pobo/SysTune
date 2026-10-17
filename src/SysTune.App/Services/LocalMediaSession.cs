using SysTune.Core.Common;
using SysTune.Core.Logging;
using SysTune.Core.Media;
using SysTune.Core.Spotify;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SysTune.App.Services;

/// <summary>
/// Reads the local Spotify desktop app through the Windows System Media Transport Controls
/// (GlobalSystemMediaTransportControlsSessionManager). Works without any Spotify login and is used as a
/// fallback when the Web API is not connected. Only sessions owned by Spotify are considered.
/// </summary>
internal sealed class LocalMediaSession : IDisposable
{
    private readonly ILog _log;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _selectGate = new(1, 1);
    private Task? _startTask;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _artKey;
    private byte[]? _art;
    private bool _disposed;

    public LocalMediaSession(ILog log, IClock clock)
    {
        _log = log;
        _clock = clock;
    }

    /// <summary>Raised on a background thread.</summary>
    public event EventHandler<NowPlaying?>? Changed;

    public NowPlaying? Current { get; private set; }

    public byte[]? Artwork => _art;

    /// <summary>Idempotent: the session manager is created once.</summary>
    public Task StartAsync() => _startTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            await SelectSessionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Windows media session API unavailable", ex);
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => _ = SelectSessionAsync();

    private static bool IsSpotify(GlobalSystemMediaTransportControlsSession s)
        => s.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true;

    private async Task SelectSessionAsync()
    {
        if (_manager is null || _disposed) return;
        await _selectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SwitchSession();
        }
        finally
        {
            _selectGate.Release();
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    private void SwitchSession()
    {
        GlobalSystemMediaTransportControlsSession? next = null;
        try
        {
            next = _manager?.GetSessions().FirstOrDefault(IsSpotify);
        }
        catch (Exception ex)
        {
            _log.Debug("GetSessions failed: " + ex.Message);
        }

        if (!ReferenceEquals(next, _session))
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }
            _session = next;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnSessionChanged;
                _session.TimelinePropertiesChanged += OnSessionChanged;
                _log.Info("Local Spotify media session found: " + _session.SourceAppUserModelId);
            }
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            NowPlaying? np = null;
            if (session is not null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                if (props is not null && !string.IsNullOrWhiteSpace(props.Title))
                {
                    var key = props.Title + "\u001f" + props.Artist;
                    // The thumbnail often arrives in a later event than the title: keep retrying until we have one.
                    if (key != _artKey || _art is null)
                    {
                        _art = await ReadThumbnailAsync(props.Thumbnail).ConfigureAwait(false);
                        _artKey = key;
                    }

                    var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
                    var position = timeline?.Position ?? TimeSpan.Zero;
                    // Timeline positions are only updated on events; extrapolate from LastUpdatedTime.
                    if (isPlaying && timeline is not null && timeline.LastUpdatedTime.Year > 2000)
                    {
                        var drift = DateTimeOffset.Now - timeline.LastUpdatedTime;
                        if (drift > TimeSpan.Zero && drift < TimeSpan.FromHours(6)) position += drift;
                    }
                    var controls = playback?.Controls;
                    np = new NowPlaying(
                        NowPlayingSource.LocalSession,
                        "local:" + key,
                        props.Title,
                        props.Artist ?? "",
                        props.AlbumTitle ?? "",
                        null,
                        (int)Math.Max(0, duration.TotalMilliseconds),
                        (int)Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, duration.TotalMilliseconds)),
                        isPlaying,
                        _clock.Elapsed,
                        "此電腦",
                        "Computer",
                        null,
                        playback?.IsShuffleActive ?? false,
                        playback?.AutoRepeatMode switch
                        {
                            Windows.Media.MediaPlaybackAutoRepeatMode.List => RepeatMode.Context,
                            Windows.Media.MediaPlaybackAutoRepeatMode.Track => RepeatMode.Track,
                            _ => RepeatMode.Off,
                        },
                        CanSeek: controls?.IsPlaybackPositionEnabled == true && duration > TimeSpan.Zero,
                        CanControl: controls?.IsPlayPauseToggleEnabled == true || controls?.IsNextEnabled == true,
                        IsAd: false);
                }
            }
            if (np is null)
            {
                _artKey = null;
                _art = null;
            }
            Current = np;
            Changed?.Invoke(this, np);
        }
        catch (Exception ex)
        {
            // Sessions disappear when Spotify exits mid-call; just treat as "no session".
            _log.Debug("Local media refresh failed: " + ex.Message);
            Current = null;
            Changed?.Invoke(this, null);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 4 * 1024 * 1024) return null;
            using var net = stream.AsStreamForRead();
            using var ms = new MemoryStream((int)stream.Size);
            await net.CopyToAsync(ms).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> TogglePlayPauseAsync() => _session is { } s && await s.TryTogglePlayPauseAsync();

    public async Task<bool> NextAsync() => _session is { } s && await s.TrySkipNextAsync();

    public async Task<bool> PreviousAsync() => _session is { } s && await s.TrySkipPreviousAsync();

    public async Task<bool> SeekAsync(int positionMs) => _session is { } s && await s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(positionMs).Ticks);

    public async Task<bool> SetShuffleAsync(bool on) => _session is { } s && await s.TryChangeShuffleActiveAsync(on);

    public async Task<bool> SetRepeatAsync(RepeatMode mode) => _session is { } s && await s.TryChangeAutoRepeatModeAsync(mode switch
    {
        RepeatMode.Context => Windows.Media.MediaPlaybackAutoRepeatMode.List,
        RepeatMode.Track => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
        _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
    });

    public void Dispose()
    {
        _disposed = true;
        if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }
        _session = null;
        _manager = null;
    }
}
