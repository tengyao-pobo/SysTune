using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysTune.Core.Settings;

namespace SysTune.App.ViewModels;

/// <summary>Root view model for the widget window.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppController _app;

    internal MainViewModel(AppController app, SystemViewModel system, NowPlayingViewModel player)
    {
        _app = app;
        System = system;
        Player = player;
        SyncFromSettings();
    }

    public SystemViewModel System { get; }

    public NowPlayingViewModel Player { get; }

    [ObservableProperty] public partial bool IsCompact { get; set; }

    [ObservableProperty] public partial double TintOpacity { get; set; } = 0.55;

    [ObservableProperty] public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty] public partial bool LockPosition { get; set; }

    [ObservableProperty] public partial bool ShowSpotify { get; set; } = true;

    [ObservableProperty] public partial bool ShowCpu { get; set; } = true;

    [ObservableProperty] public partial bool ShowGpu { get; set; } = true;

    [ObservableProperty] public partial bool ShowMemory { get; set; } = true;

    [ObservableProperty] public partial bool ShowNetwork { get; set; } = true;

    [ObservableProperty] public partial bool ShowDisks { get; set; } = true;

    /// <summary>True when any metric tile is visible (controls the grid section).</summary>
    [ObservableProperty] public partial bool ShowTiles { get; set; } = true;

    [ObservableProperty] public partial string TopmostGlyph { get; set; } = "";

    internal void SyncFromSettings()
    {
        var s = _app.Settings;
        IsCompact = s.Mode == WidgetMode.Compact;
        AlwaysOnTop = s.AlwaysOnTop;
        LockPosition = s.LockPosition;
        ShowSpotify = s.ShowSpotify;
        ShowCpu = s.ShowCpu;
        ShowGpu = s.ShowGpu;
        ShowMemory = s.ShowMemory;
        ShowNetwork = s.ShowNetwork;
        ShowDisks = s.ShowDisks;
        ShowTiles = s.ShowCpu || s.ShowGpu || s.ShowMemory || s.ShowNetwork;
        TopmostGlyph = s.AlwaysOnTop ? "" : "";
        TintOpacity = _app.EffectiveTintOpacity;
    }

    [RelayCommand]
    private void ToggleMode() => _app.SetMode(IsCompact ? WidgetMode.Expanded : WidgetMode.Compact);

    [RelayCommand]
    private void ToggleTopmost() => _app.SetAlwaysOnTop(!AlwaysOnTop);

    [RelayCommand]
    private void OpenSettings() => _app.OpenSettings();

    [RelayCommand]
    private void Hide() => _app.HideWidget();

    [RelayCommand]
    private void Exit() => _app.Exit();
}
