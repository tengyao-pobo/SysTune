using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SysTune.App.Interop;
using SysTune.Core.Settings;

namespace SysTune.App.Services;

/// <summary>
/// Applies the glass backdrop to a WPF window.
/// <para>
/// Windows are per-pixel-alpha layered windows (<c>AllowsTransparency=True</c>): this is the only WPF
/// configuration whose transparency survives both hardware and software rendering (VMs, RDP, GPUs that
/// WPF does not accelerate). The blur behind is supplied by the shell's accent policy
/// (<c>SetWindowCompositionAttribute</c>): acrylic on Windows 11 – which stays translucent while the
/// window is inactive, essential for a widget – and gaussian blur on Windows 10, where acrylic lags while
/// dragging. Layered windows don't get DWM rounded corners, so the blur is clipped with a rounded window
/// region that matches the WPF corner radius.
/// </para>
/// </summary>
internal static class WindowEffects
{
    public const double CornerRadiusDip = 8;

    public static int Build { get; } = Environment.OSVersion.Version.Build;

    public static bool IsWindows11 => Build >= 22000;

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

    /// <summary>WPF render tier (0 = software, 2 = full hardware), for diagnostics.</summary>
    public static int RenderTier => RenderCapability.Tier >> 16;

    /// <summary>Applies the effect and returns the mode that is actually in effect.</summary>
    public static GlassMode Apply(Window window, GlassMode requested, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return GlassMode.Off;

        var darkFlag = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));
        UpdateRegion(window);

        if (requested == GlassMode.Off || !TransparencyEnabled())
        {
            SetAccent(hwnd, Native.AccentState.ACCENT_DISABLED, 0);
            return GlassMode.Off;
        }

        // A faint base tint inside the blur keeps text legible over bright wallpapers; the main,
        // user-adjustable tint is drawn by WPF on top. Colour format is AABBGGRR.
        var baseTint = dark ? 0x26100C0Au : 0x26FCFAF8u;
        if (IsWindows11 && requested != GlassMode.Blur)
        {
            return SetAccent(hwnd, Native.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, baseTint) ? GlassMode.Acrylic : GlassMode.Off;
        }
        return SetAccent(hwnd, Native.AccentState.ACCENT_ENABLE_BLURBEHIND, 0) ? GlassMode.Blur : GlassMode.Off;
    }

    /// <summary>Clips the window (and therefore the blur) to rounded corners. Call after size/DPI changes.</summary>
    public static void UpdateRegion(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || window.ActualWidth <= 0) return;
        if (!IsWindows11)
        {
            Native.SetWindowRgn(hwnd, 0, true); // Windows 10 windows are square
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(window);
        var w = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var d = (int)Math.Round(CornerRadiusDip * 2 * dpi.DpiScaleX);
        var rgn = Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
        if (rgn != 0 && Native.SetWindowRgn(hwnd, rgn, true) == 0) Native.DeleteObject(rgn); // on success the system owns rgn
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

    /// <summary>
    /// Tool window (no taskbar button, not in Alt+Tab) or app window (forced taskbar button even though the
    /// window is owned by the desktop). Returns true if the style changed.
    /// </summary>
    public static bool SetToolWindow(Window window, bool toolWindow)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return false;
        var old = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        var ex = toolWindow
            ? (old | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW
            : (old & ~Native.WS_EX_TOOLWINDOW) | Native.WS_EX_APPWINDOW;
        if (ex == old) return false;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)ex);
        return true;
    }

    /// <summary>
    /// Makes the desktop (Progman, owned by Explorer) the window's owner. Owned-by-desktop windows are not
    /// hidden by "Show desktop" (Win+D / the taskbar corner), which is what a desktop widget needs, while
    /// still taking part in normal z-order. Returns false if the desktop window was not found.
    /// </summary>
    public static bool AttachToDesktop(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var progman = Native.FindWindow("Progman", null);
        if (hwnd == 0 || progman == 0) return false;
        Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, progman);
        return true;
    }
}
