using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysTune.App.Services;
using SysTune.Core.Common;
using SysTune.Core.Formatting;
using SysTune.Core.Logging;
using SysTune.Core.Media;
using SysTune.Core.Settings;
using SysTune.Core.Spotify;

namespace SysTune.App.ViewModels;

/// <summary>Presentation + commands for the music card. Combines Spotify Connect and the local media session.</summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly SpotifyPlaybackService _spotify;
    private readonly LocalMediaSession _local;
    private readonly ArtworkLoader _artwork;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly Func<AppSettings> _settings;
    private readonly Action _openSpotifySettings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _messageTimer;
    private readonly DispatcherTimer _volumeDebounce;
    private CancellationTokenSource? _artCts;
    private string? _artKey;
    private NowPlaying? _current;
    private int _pendingVolume = -1;
    private TimeSpan _userVolumeAt = TimeSpan.MinValue;

    internal NowPlayingViewModel(SpotifyPlaybackService spotify, LocalMediaSession local, ArtworkLoader artwork, IClock clock, ILog log, Func<AppSettings> settings, Action openSpotifySettings)
    {
        _spotify = spotify;
        _local = local;
        _artwork = artwork;
        _clock = clock;
        _log = log;
        _settings = settings;
        _openSpotifySettings = openSpotifySettings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _messageTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            Message = null;
        };
        _volumeDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _volumeDebounce.Tick += async (_, _) =>
        {
            _volumeDebounce.Stop();
            var v = _pendingVolume;
            if (v < 0) return;
            var deviceId = _spotify.ActiveDeviceId;
            Report(await _spotify.ExecuteAsync((api, ct) => api.SetVolumeAsync(v, deviceId, ct)));
        };

        _spotify.StateChanged += (_, _) => _dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        _local.Changed += (_, _) => _dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        Refresh();
    }

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty] public partial bool HasTrack { get; set; }

    [ObservableProperty] public partial string Title { get; set; } = "";

    [ObservableProperty] public partial string Artist { get; set; } = "";

    [ObservableProperty] public partial string Album { get; set; } = "";

    [ObservableProperty] public partial ImageSource? Artwork { get; set; }

    [ObservableProperty] public partial bool IsPlaying { get; set; }

    [ObservableProperty] public partial string PlayGlyph { get; set; } = "";

    [ObservableProperty] public partial double PositionMs { get; set; }

    [ObservableProperty] public partial double DurationMs { get; set; } = 1;

    [ObservableProperty] public partial string PositionText { get; set; } = "0:00";

    [ObservableProperty] public partial double PositionPercent { get; set; }

    [ObservableProperty] public partial string DurationText { get; set; } = "0:00";

    [ObservableProperty] public partial bool CanSeek { get; set; }

    [ObservableProperty] public partial bool CanControl { get; set; }

    [ObservableProperty] public partial string DeviceName { get; set; } = "";

    [ObservableProperty] public partial string DeviceGlyph { get; set; } = "";

    [ObservableProperty] public partial string SourceLabel { get; set; } = "";

    [ObservableProperty] public partial bool IsConnect { get; set; }

    [ObservableProperty] public partial bool Shuffle { get; set; }

    [ObservableProperty] public partial bool RepeatActive { get; set; }

    [ObservableProperty] public partial string RepeatGlyph { get; set; } = "";

    [ObservableProperty] public partial bool HasVolume { get; set; }

    [ObservableProperty] public partial double Volume { get; set; }

    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty] public partial string EmptyTitle { get; set; } = "";

    [ObservableProperty] public partial string EmptySubtitle { get; set; } = "";

    [ObservableProperty] public partial bool ShowConnectButton { get; set; }

    [ObservableProperty] public partial bool ShowOpenSpotifyButton { get; set; }

    [ObservableProperty] public partial bool IsDevicePickerOpen { get; set; }

    [ObservableProperty] public partial bool IsLoadingDevices { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";

    /// <summary>Set by the view while the user drags the seek bar, to stop the ticker fighting the drag.</summary>
    public bool IsSeeking { get; set; }

    /// <summary>Called by the UI ticker (only while visible and playing).</summary>
    public void Tick()
    {
        if (_current is null || IsSeeking) return;
        var p = _current.ProgressAt(_clock.Elapsed);
        PositionMs = p;
        PositionPercent = _current.DurationMs > 0 ? p * 100.0 / _current.DurationMs : 0;
        PositionText = Fmt.MediaTime(TimeSpan.FromMilliseconds(p));
    }

    private void Refresh()
    {
        var web = _spotify.State;
        var settings = _settings();
        var np = NowPlayingSelector.Select(web, _local.Current, settings.Spotify.UseLocalMediaSession);
        _current = np;

        StatusText = web.Status switch
        {
            SpotifyStatus.Connected => "Spotify 已連結",
            SpotifyStatus.Connecting => "等待授權…",
            SpotifyStatus.Offline => "離線，稍後重試",
            SpotifyStatus.RateLimited => "請求受限，稍後重試",
            SpotifyStatus.Error => "Spotify 錯誤",
            _ => "未連結 Spotify",
        };
        if (web.Message is { Length: > 0 } msg && web.Status != SpotifyStatus.Connected) ShowMessage(msg);

        HasTrack = np is not null;
        if (np is null)
        {
            Artwork = null;
            _artKey = null;
            var configured = !string.IsNullOrWhiteSpace(settings.Spotify.ClientId);
            switch (web.Status)
            {
                case SpotifyStatus.Connected:
                    EmptyTitle = "目前沒有播放";
                    EmptySubtitle = "在任一裝置上開始播放 Spotify";
                    ShowConnectButton = false;
                    ShowOpenSpotifyButton = true;
                    break;
                case SpotifyStatus.Connecting:
                    EmptyTitle = "等待 Spotify 授權";
                    EmptySubtitle = "請在瀏覽器中完成登入與同意";
                    ShowConnectButton = false;
                    ShowOpenSpotifyButton = false;
                    break;
                default:
                    EmptyTitle = configured ? "連結 Spotify" : "Spotify 尚未設定";
                    EmptySubtitle = configured
                        ? "連結帳戶以顯示所有裝置的播放資訊"
                        : "開啟 Spotify 桌面版即可顯示本機播放，或設定 Client ID 以使用 Spotify Connect";
                    ShowConnectButton = true;
                    ShowOpenSpotifyButton = false;
                    break;
            }
            CanControl = false;
            CanSeek = false;
            return;
        }

        Title = np.Title;
        Artist = np.Artist;
        Album = np.Album;
        IsPlaying = np.IsPlaying;
        PlayGlyph = np.IsPlaying ? "" : "";
        DurationMs = Math.Max(1, np.DurationMs);
        DurationText = np.DurationMs > 0 ? Fmt.MediaTime(TimeSpan.FromMilliseconds(np.DurationMs)) : "";
        CanSeek = np.CanSeek;
        CanControl = np.CanControl;
        IsConnect = np.Source == NowPlayingSource.SpotifyConnect;
        SourceLabel = IsConnect ? "Spotify Connect" : "本機 Spotify";
        DeviceName = np.DeviceName ?? "";
        DeviceGlyph = GlyphFor(np.DeviceType);
        Shuffle = np.Shuffle;
        RepeatActive = np.Repeat != RepeatMode.Off;
        RepeatGlyph = np.Repeat == RepeatMode.Track ? "" : "";
        HasVolume = np.VolumePercent is not null;
        // Spotify reports the old volume for a moment after a change; don't snap the slider back.
        if (np.VolumePercent is { } vol && !_volumeDebounce.IsEnabled && _clock.Elapsed - _userVolumeAt > TimeSpan.FromSeconds(3)) Volume = vol;
        Tick();
        LoadArtwork(np);
    }

    private void LoadArtwork(NowPlaying np)
    {
        // Local thumbnails can arrive in a later SMTC event than the title, so include their size in the key.
        var key = np.Source == NowPlayingSource.LocalSession ? np.ItemKey + "#" + (_local.Artwork?.Length ?? 0) : np.ArtworkUrl;
        if (key == _artKey) return;
        _artKey = key;
        _artCts?.Cancel();
        if (key is null)
        {
            Artwork = null;
            return;
        }
        if (np.Source == NowPlayingSource.LocalSession)
        {
            Artwork = _artwork.LoadBytes(key, _local.Artwork);
            return;
        }
        var cts = _artCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var img = await _artwork.LoadUrlAsync(key, cts.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested && _artKey == key) Artwork = img;
            });
        });
    }

    internal static string GlyphFor(string? type) => type?.ToLowerInvariant() switch
    {
        "computer" => "",
        "smartphone" => "",
        "tablet" => "",
        "tv" => "",
        "castvideo" or "castaudio" => "",
        "gameconsole" => "",
        "automobile" => "",
        _ => "",
    };

    // ---------------- Commands ----------------

    [RelayCommand]
    private async Task PlayPause()
    {
        if (_current is null) return;
        var wasPlaying = _current.IsPlaying;
        if (_current.Source == NowPlayingSource.LocalSession)
        {
            if (!await _local.TogglePlayPauseAsync()) ShowMessage("Spotify 桌面版拒絕此操作");
            return;
        }
        var deviceId = _spotify.ActiveDeviceId;
        Report(await _spotify.ExecuteAsync(
            (api, ct) => wasPlaying ? api.PauseAsync(deviceId, ct) : api.PlayAsync(deviceId, ct),
            p => p with { IsPlaying = !wasPlaying }));
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_current?.Source == NowPlayingSource.LocalSession)
        {
            await _local.NextAsync();
            return;
        }
        var deviceId = _spotify.ActiveDeviceId;
        Report(await _spotify.ExecuteAsync((api, ct) => api.NextAsync(deviceId, ct)));
    }

    [RelayCommand]
    private async Task Previous()
    {
        if (_current?.Source == NowPlayingSource.LocalSession)
        {
            await _local.PreviousAsync();
            return;
        }
        var deviceId = _spotify.ActiveDeviceId;
        Report(await _spotify.ExecuteAsync((api, ct) => api.PreviousAsync(deviceId, ct)));
    }

    [RelayCommand]
    private async Task ToggleShuffle()
    {
        if (_current is null) return;
        var next = !_current.Shuffle;
        if (_current.Source == NowPlayingSource.LocalSession)
        {
            await _local.SetShuffleAsync(next);
            return;
        }
        var deviceId = _spotify.ActiveDeviceId;
        Report(await _spotify.ExecuteAsync((api, ct) => api.SetShuffleAsync(next, deviceId, ct), p => p with { Shuffle = next }));
    }

    [RelayCommand]
    private async Task CycleRepeat()
    {
        if (_current is null) return;
        var next = _current.Repeat switch { RepeatMode.Off => RepeatMode.Context, RepeatMode.Context => RepeatMode.Track, _ => RepeatMode.Off };
        if (_current.Source == NowPlayingSource.LocalSession)
        {
            await _local.SetRepeatAsync(next);
            return;
        }
        var deviceId = _spotify.ActiveDeviceId;
        Report(await _spotify.ExecuteAsync((api, ct) => api.SetRepeatAsync(next, deviceId, ct), p => p with { Repeat = next }));
    }

    public async Task SeekToAsync(double positionMs)
    {
        if (_current is null || !CanSeek) return;
        var ms = (int)Math.Clamp(positionMs, 0, DurationMs);
        _current = _current with { ProgressMs = ms, CapturedAt = _clock.Elapsed };
        Tick();
        if (_current.Source == NowPlayingSource.LocalSession)
        {
            await _local.SeekAsync(ms);
            return;
        }
        var deviceId = _spotify.ActiveDeviceId;
        Report(await _spotify.ExecuteAsync((api, ct) => api.SeekAsync(ms, deviceId, ct), p => p with { ProgressMs = ms }));
    }

    /// <summary>Volume slider changes are debounced to respect API rate limits.</summary>
    public void SetVolume(double percent)
    {
        _pendingVolume = (int)Math.Round(Math.Clamp(percent, 0, 100));
        _userVolumeAt = _clock.Elapsed;
        Volume = _pendingVolume;
        _volumeDebounce.Stop();
        _volumeDebounce.Start();
    }

    [RelayCommand]
    private async Task Connect()
    {
        var settings = _settings();
        if (string.IsNullOrWhiteSpace(settings.Spotify.ClientId))
        {
            _openSpotifySettings();
            return;
        }
        _spotify.Connection.ClientId = settings.Spotify.ClientId;
        try
        {
            await _spotify.ConnectAsync(settings.Spotify.CallbackPort, OpenBrowser, CancellationToken.None);
            ShowMessage("已連結 Spotify");
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer connect attempt
        }
        catch (Exception ex)
        {
            ShowMessage(ex is SpotifyException ? ex.Message : "無法連結 Spotify，詳見記錄檔");
            if (ex is not SpotifyException) _log.Error("Connect failed", ex);
        }
    }

    private void OpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Could not open browser", ex);
            _dispatcher.BeginInvoke(() => ShowMessage("無法開啟瀏覽器"));
        }
    }

    [RelayCommand]
    private void OpenSpotify()
    {
        try
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
        }
        catch (Exception)
        {
            OpenBrowser(new Uri("https://open.spotify.com/"));
        }
    }

    [RelayCommand]
    private async Task OpenDevices()
    {
        if (!IsConnect)
        {
            ShowMessage("切換裝置需要連結 Spotify 帳戶（Spotify Connect）");
            return;
        }
        IsDevicePickerOpen = true;
        IsLoadingDevices = true;
        try
        {
            var devices = await _spotify.Api.GetDevicesAsync(CancellationToken.None);
            Devices.Clear();
            foreach (var d in devices.OrderByDescending(d => d.IsActive).ThenBy(d => d.Name, StringComparer.CurrentCulture))
            {
                Devices.Add(new DeviceItem(d.Id, d.Name, GlyphFor(d.Type), d.IsActive, d.IsRestricted || d.Id is null, d.Type));
            }
            if (Devices.Count == 0) ShowMessage("找不到可用裝置，請在要使用的裝置上開啟 Spotify");
        }
        catch (SpotifyException ex)
        {
            ShowMessage(ex.Message);
            IsDevicePickerOpen = false;
        }
        finally
        {
            IsLoadingDevices = false;
        }
    }

    [RelayCommand]
    private async Task Transfer(DeviceItem? device)
    {
        if (device?.Id is null || device.IsActive) return;
        IsDevicePickerOpen = false;
        var wasPlaying = IsPlaying;
        Report(await _spotify.ExecuteAsync((api, ct) => api.TransferAsync(device.Id, wasPlaying, ct)));
    }

    private void Report(string? error)
    {
        if (error is not null) _dispatcher.BeginInvoke(() => ShowMessage(error));
    }

    public void ShowMessage(string text)
    {
        Message = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    /// <summary>Re-evaluates the source after a settings change.</summary>
    public void Reevaluate() => Refresh();
}

public sealed record DeviceItem(string? Id, string Name, string Glyph, bool IsActive, bool IsRestricted, string Type)
{
    public bool IsSelectable => !IsActive && !IsRestricted;

    public string Detail => IsActive ? "播放中" : IsRestricted ? "此裝置不支援遠端控制" : Type;
}
