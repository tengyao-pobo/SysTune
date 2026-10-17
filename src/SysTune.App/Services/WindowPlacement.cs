using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SysTune.App.Interop;

namespace SysTune.App.Services;

/// <summary>
/// Saves/restores window position via Win32 WINDOWPLACEMENT, which is expressed in physical pixels
/// and handled by Windows across monitors with different DPI. Off-screen positions (monitor removed)
/// are detected and rejected so the widget never gets lost.
/// </summary>
internal static class WindowPlacement
{
    public static Core.Settings.WindowState? Capture(Window window, double width)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return null;
        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(hwnd, ref wp)) return null;
        var r = wp.rcNormalPosition;
        return new Core.Settings.WindowState { Width = width, Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
    }

    /// <summary>Restores the top-left corner. Returns false if the saved spot is not on any monitor.</summary>
    public static bool TryRestore(Window window, Core.Settings.WindowState state)
    {
        if (!state.HasPlacement) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return false;

        // Require the top strip of the window to be on a connected monitor.
        var probe = new Native.RECT { Left = state.Left!.Value, Top = state.Top!.Value, Right = state.Left.Value + 80, Bottom = state.Top.Value + 24 };
        if (Native.MonitorFromRect(ref probe, Native.MONITOR_DEFAULTTONULL) == 0) return false;

        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(hwnd, ref wp)) return false;
        var w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
        var h = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
        wp.rcNormalPosition = new Native.RECT { Left = state.Left.Value, Top = state.Top.Value, Right = state.Left.Value + w, Bottom = state.Top.Value + h };
        wp.showCmd = window.IsVisible ? Native.SW_SHOWNOACTIVATE : 0 /* SW_HIDE */;
        wp.flags = 0;
        return Native.SetWindowPlacement(hwnd, ref wp);
    }

    /// <summary>Places the window at the top-right of the primary work area.</summary>
    public static void PlaceDefault(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 24;
        window.Top = area.Top + 24;
    }
}
