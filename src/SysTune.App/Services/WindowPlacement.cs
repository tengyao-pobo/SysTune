using System.Windows;
using System.Windows.Interop;
using SysTune.App.Interop;

namespace SysTune.App.Services;

/// <summary>
/// Saves/restores the window's top-left corner in physical screen pixels (GetWindowRect/SetWindowPos).
/// Screen coordinates are unambiguous across monitors, taskbar positions and tool-window styles; when the
/// window lands on a monitor with a different DPI, Windows sends WM_DPICHANGED and WPF rescales it.
/// Positions that are no longer on any monitor (display unplugged) are rejected so the widget never gets lost.
/// </summary>
internal static class WindowPlacement
{
    public static Core.Settings.WindowState? Capture(Window window, double width)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !Native.GetWindowRect(hwnd, out var r)) return null;
        return new Core.Settings.WindowState { Width = width, Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
    }

    /// <summary>Restores the top-left corner. Returns false if the saved spot is not on any monitor.</summary>
    public static bool TryRestore(Window window, Core.Settings.WindowState state)
    {
        if (!state.HasPlacement) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return false;

        // Require the top strip of the window (where it can be grabbed) to be on a connected monitor.
        var probe = new Native.RECT { Left = state.Left!.Value, Top = state.Top!.Value, Right = state.Left.Value + 80, Bottom = state.Top.Value + 24 };
        if (Native.MonitorFromRect(ref probe, Native.MONITOR_DEFAULTTONULL) == 0) return false;

        return Native.SetWindowPos(hwnd, 0, state.Left.Value, state.Top.Value, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Places the window at the top-right of the primary work area.</summary>
    public static void PlaceDefault(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 24;
        window.Top = area.Top + 24;
    }
}
