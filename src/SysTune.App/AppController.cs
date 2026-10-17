using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SysTune.App.Monitoring;
using SysTune.App.Services;
using SysTune.App.ViewModels;
using SysTune.App.Views;
using SysTune.Core.Common;
using SysTune.Core.Logging;
using SysTune.Core.Monitoring;
using SysTune.Core.Settings;
using SysTune.Core.Spotify;

namespace SysTune.App;

/// <summary>
/// Composition root and coordinator: owns settings, services, the widget window, the tray icon and
/// the settings window, and applies setting changes to all of them.
/// </summary>
internal sealed class AppController : IDisposable
{
    [Flags]
    internal enum Area
    {
        None = 0,
        Appearance = 1,
        Window = 2,
        Startup = 4,
        Monitoring = 8,
        Layout = 16,
        Spotify = 32,

        /// <summary>Tint opacity only: cheap, no backdrop re-application (the slider fires continuously).</summary>
        Tint = 64,
    }

    private readonly SettingsStore _store;
    private readonly ThemeService _theme = new();
    private readonly SystemMonitor _monitor;
    private readonly HttpClient _http;
    private readonly SpotifyConnection _connection;
    private readonly LocalMediaSession _local;
    private readonly DispatcherTimer _ticker;
    private readonly SystemViewModel _systemVm = new();
    private MainWindow? _window;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private GlassMode _effectiveGlass = GlassMode.Acrylic;
    private SystemSnapshot? _lastSnapshot;
    private int _pendingSnapshot;
    private bool _exiting;
    private bool _glassLogged;

    public AppController(ILog log, SettingsStore store)
    {
        Log = log;
        _store = store;
        _monitor = new SystemMonitor(log);
        _http = CreateHttpClient();
        _connection = new SpotifyConnection(new SpotifyAuthClient(_http), CreateTokenStore(log), SystemClock.Instance, log)
        {
            ClientId = Settings.Spotify.ClientId,
        };
        Spotify = new SpotifyPlaybackService(_connection, new SpotifyApiClient(_http, _connection), SystemClock.Instance, log);
        _local = new LocalMediaSession(log, SystemClock.Instance);
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += (_, _) => Player?.Tick();
    }

    public ILog Log { get; }

    public AppSettings Settings => _store.Current;

    public SpotifyPlaybackService Spotify { get; }

    public MainViewModel? ViewModel { get; private set; }

    public NowPlayingViewModel? Player { get; private set; }

    /// <summary>Tint opacity actually used: opaque when no blur is available.</summary>
    public double EffectiveTintOpacity => _effectiveGlass == GlassMode.Off ? 1.0 : Settings.TintOpacity;

    /// <summary>Latest snapshot (used by the CI smoke test to verify real data).</summary>
    public SystemSnapshot? LastSnapshot => Volatile.Read(ref _lastSnapshot);

    /// <summary>Average sampling cost per data source (ms).</summary>
    public Dictionary<string, double> MonitorTimings => _monitor.GetTimings();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SysTune/1.0 (+Windows desktop widget)");
        return http;
    }

    private static ITokenStore CreateTokenStore(ILog log)
    {
        try
        {
            return new DpapiTokenStore(AppPaths.TokenFile);
        }
        catch (Exception ex)
        {
            log.Warn("Secure token storage unavailable; Spotify login will not persist", ex);
            return new MemoryTokenStore();
        }
    }

    public void Start(bool autostart)
    {
        _theme.Apply(Settings.Theme);
        _theme.ThemeChanged += (_, _) =>
        {
            ApplyGlass();
            _tray?.UpdateIcon();
        };

        Player = new NowPlayingViewModel(Spotify, _local, new ArtworkLoader(_http, Log), SystemClock.Instance, Log, () => Settings, () => OpenSettings(focusSpotify: true));
        _systemVm.UseFahrenheit = Settings.UseFahrenheit;
        ViewModel = new MainViewModel(this, _systemVm, Player);

        _window = new MainWindow(this) { DataContext = ViewModel };
        _window.IsVisibleChanged += (_, _) => OnWidgetVisibilityChanged();

        _tray = new TrayIcon(_window, BuildTrayMenu);
        _tray.LeftClick += (_, _) => ToggleWidget();

        _monitor.IntervalMs = Settings.RefreshIntervalMs;
        _monitor.EnableGpu = Settings.ShowGpu;
        _monitor.SnapshotReady += OnSnapshot;
        _monitor.Start();

        Spotify.Start();
        if (Settings.Spotify.UseLocalMediaSession) _ = _local.StartAsync();

        AutoStart.Apply(Settings.StartWithWindows, Log);

        if (autostart && Settings.StartHidden)
        {
            OnWidgetVisibilityChanged();
            Log.Info("Started hidden (autostart)");
        }
        else
        {
            ShowWidget();
        }
    }

    // ---------------- Monitoring ----------------

    private void OnSnapshot(object? sender, SystemSnapshot snapshot)
    {
        Volatile.Write(ref _lastSnapshot, snapshot);
        // Coalesce: if the UI is busy, skip intermediate snapshots instead of queueing them.
        if (Interlocked.Exchange(ref _pendingSnapshot, 1) == 1) return;
        _window?.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pendingSnapshot, 0);
            if (Volatile.Read(ref _lastSnapshot) is { } s)
            {
                _systemVm.Apply(s);
                _tray?.SetTooltip("SysTune\n" + _systemVm.Summary);
            }
        }, DispatcherPriority.Background);
    }

    private void OnWidgetVisibilityChanged()
    {
        var visible = _window?.IsVisible == true;
        // When hidden, only the tray tooltip needs data: sample slowly instead of stopping entirely.
        _monitor.IntervalMs = visible ? Settings.RefreshIntervalMs : 5000;
        Spotify.IsActive = visible && Settings.ShowSpotify;
        if (visible) _ticker.Start();
        else _ticker.Stop();
    }

    // ---------------- Window management ----------------

    public void ShowWidget()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized) _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    public void HideWidget() => _window?.Hide();

    public void ToggleWidget()
    {
        if (_window?.IsVisible == true) HideWidget();
        else ShowWidget();
    }

    public void SetMode(WidgetMode mode)
    {
        if (Settings.Mode == mode || _window is null) return;
        _window.SavePlacement();
        Settings.Mode = mode;
        _store.SaveDebounced();
        ViewModel?.SyncFromSettings();
        _window.ApplyMode();
    }

    public void SetAlwaysOnTop(bool value)
    {
        Settings.AlwaysOnTop = value;
        ApplySettings(Area.Window);
        SyncSettingsWindow();
    }

    /// <summary>Refreshes the settings window after a change made from the widget or tray.</summary>
    private void SyncSettingsWindow()
    {
        if (_settingsWindow?.DataContext is SettingsViewModel vm) vm.Reload();
    }

    public void ResetPosition()
    {
        Settings.Expanded.Left = Settings.Expanded.Top = Settings.Expanded.Right = Settings.Expanded.Bottom = null;
        Settings.Compact.Left = Settings.Compact.Top = Settings.Compact.Right = Settings.Compact.Bottom = null;
        _window?.ResetPosition();
        _store.SaveDebounced();
    }

    public void SavePlacement(Core.Settings.WindowState state, WidgetMode mode)
    {
        var target = mode == WidgetMode.Compact ? Settings.Compact : Settings.Expanded;
        target.Width = state.Width;
        target.Left = state.Left;
        target.Top = state.Top;
        target.Right = state.Right;
        target.Bottom = state.Bottom;
        _store.SaveDebounced();
    }

    public void ApplyGlass()
    {
        if (_window is null) return;
        var effective = _window.ApplyGlass(Settings.Glass, _theme.IsDark);
        if (effective != _effectiveGlass || !_glassLogged)
        {
            Log.Info($"Glass: requested {Settings.Glass}, effective {effective} (transparency effects {(WindowEffects.TransparencyEnabled() ? "on" : "off")}, build {WindowEffects.Build})");
            _glassLogged = true;
        }
        _effectiveGlass = effective;
        _settingsWindow?.ApplyGlass(Settings.Glass, _theme.IsDark);
        ViewModel?.SyncFromSettings();
    }

    public void OpenSettings(bool focusSpotify = false)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this) { DataContext = new SettingsViewModel(this) };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        else if (_settingsWindow.DataContext is SettingsViewModel vm)
        {
            vm.Reload();
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
        if (focusSpotify) _settingsWindow.FocusSpotifySection();
    }

    /// <summary>Applies changed settings to the running app and persists them.</summary>
    public void ApplySettings(Area area)
    {
        Settings.Normalize();
        if (area.HasFlag(Area.Appearance))
        {
            _theme.Apply(Settings.Theme);
            ApplyGlass();
        }
        if (area.HasFlag(Area.Tint)) _settingsWindow?.UpdateTint();
        if (area.HasFlag(Area.Window)) _window?.ApplyWindowSettings();
        if (area.HasFlag(Area.Startup)) AutoStart.Apply(Settings.StartWithWindows, Log);
        if (area.HasFlag(Area.Monitoring))
        {
            _systemVm.UseFahrenheit = Settings.UseFahrenheit;
            _monitor.EnableGpu = Settings.ShowGpu;
            OnWidgetVisibilityChanged();
        }
        if (area.HasFlag(Area.Spotify))
        {
            _connection.ClientId = Settings.Spotify.ClientId;
            if (Settings.Spotify.UseLocalMediaSession && _local.Current is null) _ = _local.StartAsync();
            Spotify.Nudge();
            Player?.Reevaluate();
        }
        if (area.HasFlag(Area.Layout)) OnWidgetVisibilityChanged();
        ViewModel?.SyncFromSettings();
        _store.SaveDebounced();
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action onClick, bool? isChecked = null)
        {
            var item = new MenuItem { Header = header, IsCheckable = isChecked is not null, IsChecked = isChecked ?? false };
            item.Click += (_, _) => onClick();
            return item;
        }

        menu.Items.Add(Item(_window?.IsVisible == true ? "隱藏小工具" : "顯示小工具", ToggleWidget));
        menu.Items.Add(Item("精簡模式", () => SetMode(Settings.Mode == WidgetMode.Compact ? WidgetMode.Expanded : WidgetMode.Compact), Settings.Mode == WidgetMode.Compact));
        menu.Items.Add(Item("永遠置頂", () => SetAlwaysOnTop(!Settings.AlwaysOnTop), Settings.AlwaysOnTop));
        menu.Items.Add(Item("鎖定位置", () =>
        {
            Settings.LockPosition = !Settings.LockPosition;
            ApplySettings(Area.Window);
            SyncSettingsWindow();
        }, Settings.LockPosition));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("設定…", () => OpenSettings()));
        menu.Items.Add(Item("重設位置", ResetPosition));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("結束 SysTune", Exit));
        return menu;
    }

    public bool IsExiting => _exiting;

    public void Exit() => Exit(0);

    public void Exit(int exitCode)
    {
        if (_exiting) return;
        _exiting = true;
        _window?.SavePlacement();
        _settingsWindow?.Close();
        Application.Current.Shutdown(exitCode);
    }

    public void Dispose()
    {
        _ticker.Stop();
        _monitor.SnapshotReady -= OnSnapshot;
        _monitor.Dispose();
        try
        {
            Spotify.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _connection.Dispose();
        _local.Dispose();
        _tray?.Dispose();
        _theme.Dispose();
        _http.Dispose();
        _store.Save();
    }
}
