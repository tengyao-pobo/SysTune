using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SysTune.App.Interop;
using SysTune.Core.Settings;

namespace SysTune.App.Services;

/// <summary>
/// Applies the glass backdrop to a WPF window.
/// The window keeps a DWM-extended frame (WindowChrome GlassFrameThickness=-1, non-layered for performance)
/// and gets one of:
/// <list type="bullet">
/// <item>Acrylic: accent policy acrylic blur on Windows 11 (stays translucent when the window is inactive,
/// which matters for a widget that is almost never focused); plain blur on Windows 10, where acrylic
/// lags while dragging.</item>
/// <item>Blur: accent policy gaussian blur.</item>
/// <item>SystemBackdrop: documented DWMWA_SYSTEMBACKDROP_TYPE acrylic (Windows 11 22H2+).</item>
/// <item>Off: no blur; the WPF tint layer is drawn opaque.</item>
/// </list>
/// Rounded corners use the documented DWMWA_WINDOW_CORNER_PREFERENCE on Windows 11.
/// </summary>
internal static class WindowEffects
{
    public static int Build { get; } = Environment.OSVersion.Version.Build;

    public static bool IsWindows11 => Build >= 22000;

    public static bool SupportsSystemBackdrop => Build >= 22621;

    /// <summary>Windows "Transparency effects" toggle (Settings → Personalization → Colors).</summary>
    public static bool TransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Applies the effect and returns the mode that is actually in effect.</summary>
    public static GlassMode Apply(Window window, GlassMode requested, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return GlassMode.Off;

        if (IsWindows11)
        {
            var corner = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        var darkFlag = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        // Make sure the whole client area is DWM "glass" so the backdrop shows through transparent pixels.
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        // Reset everything first so switching modes is clean.
        SetAccent(hwnd, Native.AccentState.ACCENT_DISABLED, 0);
        if (SupportsSystemBackdrop)
        {
            var none = Native.DWMSBT_NONE;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }

        if (!TransparencyEnabled()) return GlassMode.Off;

        switch (requested)
        {
            case GlassMode.SystemBackdrop when SupportsSystemBackdrop:
                var type = Native.DWMSBT_TRANSIENTWINDOW;
                return Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0 ? GlassMode.SystemBackdrop : GlassMode.Off;
            case GlassMode.SystemBackdrop:
            case GlassMode.Acrylic:
                if (IsWindows11)
                {
                    // Near-transparent gradient: the visible tint is drawn by WPF so the opacity slider controls it.
                    return SetAccent(hwnd, Native.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, dark ? 0x01141414u : 0x01F4F4F4u) ? GlassMode.Acrylic : GlassMode.Off;
                }
                return SetAccent(hwnd, Native.AccentState.ACCENT_ENABLE_BLURBEHIND, 0) ? GlassMode.Blur : GlassMode.Off;
            case GlassMode.Blur:
                return SetAccent(hwnd, Native.AccentState.ACCENT_ENABLE_BLURBEHIND, 0) ? GlassMode.Blur : GlassMode.Off;
            default:
                return GlassMode.Off;
        }
    }

    private static bool SetAccent(nint hwnd, Native.AccentState state, uint gradient)
    {
        var accent = new Native.AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = gradient };
        var size = Marshal.SizeOf<Native.AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Native.WindowCompositionAttributeData
            {
                Attribute = Native.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            return Native.SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Hides the window from Alt+Tab (tool window) unless it should appear in the taskbar.</summary>
    public static void SetToolWindow(Window window, bool toolWindow)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        ex = toolWindow ? (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW : ex & ~Native.WS_EX_TOOLWINDOW;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)ex);
    }
}
