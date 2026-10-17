using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysTune.App.Services;
using SysTune.Core.Settings;
using SysTune.Core.Spotify;

namespace SysTune.App.ViewModels;

/// <summary>Two-way view of <see cref="AppSettings"/>; every change is applied live and persisted.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppController _app;
    private bool _loading;

    internal SettingsViewModel(AppController app)
    {
        _app = app;
        Load();
        _app.Spotify.StateChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshSpotifyStatus);
        RefreshSpotifyStatus();
    }

    private AppSettings S => _app.Settings;

    [ObservableProperty] public partial ThemeMode Theme { get; set; }

    [ObservableProperty] public partial GlassMode Glass { get; set; }

    [ObservableProperty] public partial double TintOpacityPercent { get; set; }

    [ObservableProperty] public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty] public partial bool StartWithWindows { get; set; }

    [ObservableProperty] public partial bool StartHidden { get; set; }

    [ObservableProperty] public partial bool ShowInTaskbar { get; set; }

    [ObservableProperty] public partial bool LockPosition { get; set; }

    [ObservableProperty] public partial bool UseFahrenheit { get; set; }

    [ObservableProperty] public partial string RefreshInterval { get; set; } = "1000";

    [ObservableProperty] public partial bool ShowSpotify { get; set; }

    [ObservableProperty] public partial bool ShowCpu { get; set; }

    [ObservableProperty] public partial bool ShowGpu { get; set; }

    [ObservableProperty] public partial bool ShowMemory { get; set; }

    [ObservableProperty] public partial bool ShowNetwork { get; set; }

    [ObservableProperty] public partial bool ShowDisks { get; set; }

    [ObservableProperty] public partial string ClientId { get; set; } = "";

    [ObservableProperty] public partial string CallbackPort { get; set; } = "";

    [ObservableProperty] public partial bool UseLocalMediaSession { get; set; }

    [ObservableProperty] public partial string SpotifyStatusText { get; set; } = "";

    [ObservableProperty] public partial bool IsSpotifyConnected { get; set; }

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string? SpotifyError { get; set; }

    public string RedirectUri => S.Spotify.RedirectUri;

    public string GlassNote => WindowEffects.IsWindows11
        ? (WindowEffects.SupportsSystemBackdrop ? "壓克力：未聚焦時仍保持透明（建議）。系統背景：Windows 原生材質，未聚焦時會變為不透明。" : "壓克力：未聚焦時仍保持透明（建議）。")
        : "Windows 10 使用模糊效果；壓克力在 Windows 10 拖曳時會延遲，因此自動改用模糊。";

    public bool SupportsSystemBackdrop => WindowEffects.SupportsSystemBackdrop;

    public string VersionText => "版本 " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0");

    public string SensorNote => Monitoring.HardwareSensors.IsElevated
        ? "以系統管理員身分執行：已嘗試讀取 CPU 感測器（需已安裝 PawnIO 驅動）。"
        : "CPU 封裝溫度需以系統管理員身分執行並安裝 PawnIO 驅動；目前顯示 Windows 提供的 ACPI 熱區（若有）。GPU 溫度不需管理員權限（NVIDIA / AMD）。";

    private void Load()
    {
        _loading = true;
        Theme = S.Theme;
        Glass = S.Glass;
        TintOpacityPercent = Math.Round(S.TintOpacity * 100);
        AlwaysOnTop = S.AlwaysOnTop;
        StartWithWindows = S.StartWithWindows;
        StartHidden = S.StartHidden;
        ShowInTaskbar = S.ShowInTaskbar;
        LockPosition = S.LockPosition;
        UseFahrenheit = S.UseFahrenheit;
        RefreshInterval = S.RefreshIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowSpotify = S.ShowSpotify;
        ShowCpu = S.ShowCpu;
        ShowGpu = S.ShowGpu;
        ShowMemory = S.ShowMemory;
        ShowNetwork = S.ShowNetwork;
        ShowDisks = S.ShowDisks;
        ClientId = S.Spotify.ClientId;
        CallbackPort = S.Spotify.CallbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UseLocalMediaSession = S.Spotify.UseLocalMediaSession;
        _loading = false;
    }

    /// <summary>Re-reads values changed elsewhere (tray menu, widget buttons).</summary>
    internal void Reload() => Load();

    partial void OnThemeChanged(ThemeMode value) => Change(() => S.Theme = value, AppController.Area.Appearance);

    partial void OnGlassChanged(GlassMode value) => Change(() => S.Glass = value, AppController.Area.Appearance);

    partial void OnTintOpacityPercentChanged(double value) => Change(() => S.TintOpacity = value / 100.0, AppController.Area.Appearance);

    partial void OnAlwaysOnTopChanged(bool value) => Change(() => S.AlwaysOnTop = value, AppController.Area.Window);

    partial void OnStartWithWindowsChanged(bool value) => Change(() => S.StartWithWindows = value, AppController.Area.Startup);

    partial void OnStartHiddenChanged(bool value) => Change(() => S.StartHidden = value, AppController.Area.None);

    partial void OnShowInTaskbarChanged(bool value) => Change(() => S.ShowInTaskbar = value, AppController.Area.Window);

    partial void OnLockPositionChanged(bool value) => Change(() => S.LockPosition = value, AppController.Area.Window);

    partial void OnUseFahrenheitChanged(bool value) => Change(() => S.UseFahrenheit = value, AppController.Area.Monitoring);

    partial void OnRefreshIntervalChanged(string value) => Change(() => S.RefreshIntervalMs = int.TryParse(value, out var ms) ? ms : 1000, AppController.Area.Monitoring);

    partial void OnShowSpotifyChanged(bool value) => Change(() => S.ShowSpotify = value, AppController.Area.Layout);

    partial void OnShowCpuChanged(bool value) => Change(() => S.ShowCpu = value, AppController.Area.Layout);

    partial void OnShowGpuChanged(bool value) => Change(() => S.ShowGpu = value, AppController.Area.Layout | AppController.Area.Monitoring);

    partial void OnShowMemoryChanged(bool value) => Change(() => S.ShowMemory = value, AppController.Area.Layout);

    partial void OnShowNetworkChanged(bool value) => Change(() => S.ShowNetwork = value, AppController.Area.Layout);

    partial void OnShowDisksChanged(bool value) => Change(() => S.ShowDisks = value, AppController.Area.Layout);

    partial void OnClientIdChanged(string value) => Change(() => S.Spotify.ClientId = value.Trim(), AppController.Area.Spotify);

    partial void OnCallbackPortChanged(string value)
    {
        if (int.TryParse(value, out var port) && port is >= 1024 and <= 65535)
        {
            Change(() => S.Spotify.CallbackPort = port, AppController.Area.Spotify);
            OnPropertyChanged(nameof(RedirectUri));
        }
    }

    partial void OnUseLocalMediaSessionChanged(bool value) => Change(() => S.Spotify.UseLocalMediaSession = value, AppController.Area.Spotify);

    private void Change(Action apply, AppController.Area area)
    {
        if (_loading) return;
        apply();
        _app.ApplySettings(area);
    }

    private void RefreshSpotifyStatus()
    {
        var state = _app.Spotify.State;
        IsSpotifyConnected = _app.Spotify.Connection.IsConnected;
        SpotifyStatusText = state.Status switch
        {
            SpotifyStatus.Connected => "已連結",
            SpotifyStatus.Connecting => "等待瀏覽器授權…",
            SpotifyStatus.Offline => "已連結（目前離線）",
            SpotifyStatus.RateLimited => "已連結（請求受限，稍後重試）",
            SpotifyStatus.Error => "錯誤：" + state.Message,
            SpotifyStatus.NotConfigured => "尚未設定 Client ID",
            _ => "未連結",
        };
        IsBusy = state.Status == SpotifyStatus.Connecting;
    }

    [RelayCommand]
    private async Task ConnectSpotify()
    {
        SpotifyError = null;
        if (string.IsNullOrWhiteSpace(S.Spotify.ClientId))
        {
            SpotifyError = "請先輸入 Client ID";
            return;
        }
        _app.Spotify.Connection.ClientId = S.Spotify.ClientId;
        try
        {
            await _app.Spotify.ConnectAsync(S.Spotify.CallbackPort, uri => Open(uri.AbsoluteUri), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer connect attempt
        }
        catch (SpotifyException ex)
        {
            SpotifyError = ex.Message;
        }
        catch (Exception ex)
        {
            SpotifyError = "連結失敗，詳見記錄檔";
            _app.Log.Write(Core.Logging.LogLevel.Error, "Spotify connect failed", ex);
        }
        RefreshSpotifyStatus();
    }

    [RelayCommand]
    private void DisconnectSpotify()
    {
        _app.Spotify.Disconnect();
        RefreshSpotifyStatus();
    }

    [RelayCommand]
    private void CopyRedirectUri()
    {
        try
        {
            System.Windows.Clipboard.SetText(RedirectUri);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy
        }
    }

    [RelayCommand]
    private static void OpenDashboard() => Open("https://developer.spotify.com/dashboard");

    [RelayCommand]
    private static void OpenGuide() => Open("https://github.com/Tglobalcorp-MIS/SysTune/blob/main/docs/SPOTIFY_SETUP.md");

    [RelayCommand]
    private static void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Open(AppPaths.LogDir);
    }

    [RelayCommand]
    private void ResetPosition() => _app.ResetPosition();

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // nothing sensible to do
        }
    }
}
