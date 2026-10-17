using System.Text.Json.Serialization;

namespace SysTune.Core.Settings;

public enum ThemeMode { System, Dark, Light }

public enum GlassMode
{
    /// <summary>Acrylic blur (Windows 11) / blur (Windows 10).</summary>
    Acrylic,

    /// <summary>Plain gaussian blur behind the window (lighter weight).</summary>
    Blur,

    /// <summary>Legacy value kept for settings compatibility; treated as <see cref="Acrylic"/>.</summary>
    SystemBackdrop,

    /// <summary>No blur, semi-transparent tinted background.</summary>
    Off,
}

public enum WidgetMode { Expanded, Compact }

/// <summary>Persisted user preferences. Plain data; call <see cref="Normalize"/> after loading.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public const double ExpandedBaseWidth = 340;
    public const double CompactBaseWidth = 460;

    /// <summary>The widget scales (zooms) between these factors of its base width.</summary>
    public const double MinScale = 0.85;
    public const double MaxScale = 1.6;

    public int Version { get; set; } = CurrentVersion;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public GlassMode Glass { get; set; } = GlassMode.Acrylic;

    /// <summary>Opacity of the tint layer on top of the glass, 0.2 – 1.0.</summary>
    public double TintOpacity { get; set; } = 0.35;

    public bool AlwaysOnTop { get; set; }

    public bool StartWithWindows { get; set; }

    public bool StartHidden { get; set; }

    public WidgetMode Mode { get; set; } = WidgetMode.Expanded;

    /// <summary>Hides the widget from Alt+Tab / taskbar (it always lives in the notification area).</summary>
    public bool ShowInTaskbar { get; set; }

    /// <summary>Locks position (disables dragging).</summary>
    public bool LockPosition { get; set; }

    /// <summary>System sampling interval in milliseconds.</summary>
    public int RefreshIntervalMs { get; set; } = 1000;

    public bool UseFahrenheit { get; set; }

    public bool ShowSpotify { get; set; } = true;

    public bool ShowCpu { get; set; } = true;

    public bool ShowGpu { get; set; } = true;

    public bool ShowMemory { get; set; } = true;

    public bool ShowNetwork { get; set; } = true;

    public bool ShowDisks { get; set; } = true;

    public SpotifySettings Spotify { get; set; } = new();

    public WindowState Expanded { get; set; } = new() { Width = 340 };

    public WindowState Compact { get; set; } = new() { Width = 460 };

    public void Normalize()
    {
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.System;
        if (!Enum.IsDefined(Glass)) Glass = GlassMode.Acrylic;
        if (!Enum.IsDefined(Mode)) Mode = WidgetMode.Expanded;
        TintOpacity = double.IsNaN(TintOpacity) ? 0.35 : Math.Clamp(TintOpacity, 0.1, 1.0);
        RefreshIntervalMs = Math.Clamp(RefreshIntervalMs, 500, 10_000);
        Spotify ??= new SpotifySettings();
        Spotify.Normalize();
        Expanded ??= new WindowState();
        Compact ??= new WindowState();
        Expanded.Width = ClampWidth(Expanded.Width, ExpandedBaseWidth * MinScale, ExpandedBaseWidth * MaxScale, ExpandedBaseWidth);
        Compact.Width = ClampWidth(Compact.Width, CompactBaseWidth * MinScale, CompactBaseWidth * MaxScale, CompactBaseWidth);
        Version = CurrentVersion;
    }

    private static double ClampWidth(double value, double min, double max, double fallback)
        => double.IsNaN(value) || value <= 0 ? fallback : Math.Clamp(value, min, max);
}

public sealed class SpotifySettings
{
    public const int DefaultPort = 47821;

    /// <summary>Client ID of the user's own Spotify developer app. Not a secret (PKCE flow).</summary>
    public string ClientId { get; set; } = "";

    public int CallbackPort { get; set; } = DefaultPort;

    /// <summary>Fall back to the local Windows media session when the Web API is not connected.</summary>
    public bool UseLocalMediaSession { get; set; } = true;

    [JsonIgnore]
    public string RedirectUri => $"http://127.0.0.1:{CallbackPort}/callback";

    public void Normalize()
    {
        ClientId = (ClientId ?? "").Trim();
        if (CallbackPort is < 1024 or > 65535) CallbackPort = DefaultPort;
    }
}

/// <summary>Saved window position. Placement is stored in physical pixels via Win32 WINDOWPLACEMENT so that
/// multi-monitor / mixed-DPI layouts restore correctly.</summary>
public sealed class WindowState
{
    public double Width { get; set; }

    public int? Left { get; set; }

    public int? Top { get; set; }

    public int? Right { get; set; }

    public int? Bottom { get; set; }

    [JsonIgnore]
    public bool HasPlacement => Left is not null && Top is not null && Right is not null && Bottom is not null && Right > Left && Bottom > Top;
}
