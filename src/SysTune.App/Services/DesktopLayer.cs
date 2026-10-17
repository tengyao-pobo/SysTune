using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using SysTune.App.Interop;
using SysTune.Core.Logging;

namespace SysTune.App.Services;

/// <summary>
/// Keeps a window on the "desktop layer": under every application window, yet still visible while
/// Windows shows the desktop (Win+D / the taskbar corner).
/// <para>
/// "Show desktop" does not hide windows; Explorer raises the desktop-icons host (Progman on Windows 11
/// 24H2+, the WorkerW holding SHELLDLL_DefView before that) above all normal windows. This class follows
/// Rainmeter's proven approach: a hidden probe window is pinned to the bottom of the z-order; when the
/// desktop host appears above the probe, Show desktop is active and the widget is lifted just above the
/// host (below the taskbar). When the host drops back, the widget returns to the bottom.
/// </para>
/// </summary>
internal sealed class DesktopLayer : IDisposable
{
    private const string ProbeTitle = "SysTune.DesktopProbe";
    private const uint ZposFlags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOOWNERZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSENDCHANGING;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly nint _widget;
    private readonly ILog _log;
    private readonly HwndSource _probe;
    private readonly DispatcherTimer _timer;
    private readonly Native.WinEventDelegate _hookProc; // kept alive for the native hook
    private readonly nint _hook;
    private bool _showDesktop;
    private bool _enabled = true;

    public DesktopLayer(nint widget, ILog log)
    {
        _widget = widget;
        _log = log;
        _probe = new HwndSource(new HwndSourceParameters(ProbeTitle)
        {
            WindowStyle = unchecked((int)(0x80000000 | 0x08000000)), // WS_POPUP | WS_DISABLED, never shown
            ExtendedWindowStyle = 0x00000080 | 0x08000000,            // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            Width = 0,
            Height = 0,
        });
        _probe.AddHook(BlockForeignZOrder);
        SetPos(_probe.Handle, Native.HWND_BOTTOM);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();

        _hookProc = OnWinEvent;
        _hook = Native.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>True while Windows is showing the desktop.</summary>
    public bool IsShowingDesktop => _showDesktop;

    /// <summary>Disabled when the widget is "always on top"; the widget is then left alone.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
            {
                _showDesktop = false;
                Check();
                if (!_showDesktop) SendToBottom();
            }
        }
    }

    /// <summary>Puts the widget at the bottom of the z-order (normal desktop-layer position).</summary>
    public void SendToBottom()
    {
        if (!_enabled) return;
        SetPos(_widget, Native.HWND_NOTOPMOST);
        SetPos(_widget, Native.HWND_BOTTOM);
    }

    /// <summary>Hook for the widget's WndProc: vetoes z-order changes made by anyone but us.</summary>
    public void FilterWindowPosChanging(nint lParam)
    {
        if (!_enabled) return;
        var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
        pos.flags |= Native.SWP_NOZORDER;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    private nint BlockForeignZOrder(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            pos.flags |= Native.SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return 0;
    }

    private void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_enabled || _showDesktop) return;
        var host = GetDesktopIconsHost();
        if (host == 0 || (hwnd != host && hwnd != Native.GetShellWindow())) return;
        // Explorer finishes re-ordering shortly after the foreground change; re-check a few times.
        Check();
        foreach (var delay in new[] { 16, 50, 120 })
        {
            var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(delay) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!_showDesktop) Check();
            };
            t.Start();
        }
    }

    private void Check()
    {
        if (!_enabled) return;
        var host = GetDesktopIconsHost();
        var shown = host != 0 && Native.IsWindowVisible(host) && Native.FindWindowEx(0, host, null, ProbeTitle) == _probe.Handle;
        if (shown == _showDesktop) return;
        _showDesktop = shown;
        _timer.Interval = TimeSpan.FromMilliseconds(shown ? 100 : 250);
        _log.Info(shown ? "Show desktop detected: lifting widget above the desktop" : "Show desktop ended: widget back on the desktop layer");
        if (shown) LiftAboveDesktop(host);
        else SendToBottom();
    }

    /// <summary>Places the widget directly under the backmost topmost window (e.g. taskbar) above the host.</summary>
    private void LiftAboveDesktop(nint host)
    {
        for (var w = Native.GetWindow(host, Native.GW_HWNDPREV); w != 0; w = Native.GetWindow(w, Native.GW_HWNDPREV))
        {
            if (w == _widget) continue;
            if ((Native.GetWindowLongPtr(w, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0 && SetPos(_widget, w)) return;
        }
        SetPos(_widget, Native.HWND_TOPMOST);
    }

    /// <summary>The window that hosts the desktop icons (same rules as Rainmeter).</summary>
    internal static nint GetDesktopIconsHost()
    {
        var shell = Native.GetShellWindow();
        if (shell == 0) return 0; // Explorer not running
        if (Native.FindWindowEx(shell, 0, "SHELLDLL_DefView", null) != 0)
        {
            // 24H2+: icons live directly in Progman, which is what gets raised.
            return Is24H2OrLater ? shell : 0;
        }
        nint worker = 0;
        while ((worker = Native.FindWindowEx(0, worker, "WorkerW", null)) != 0)
        {
            if (Native.IsWindowVisible(worker) && Native.FindWindowEx(worker, 0, "SHELLDLL_DefView", null) != 0) return worker;
        }
        return 0;
    }

    // GetCurrentMonitorTopologyId only exists on builds with the 24H2 desktop hierarchy.
    private static readonly bool Is24H2OrLater = NativeLibrary.TryGetExport(NativeLibrary.Load("user32.dll"), "GetCurrentMonitorTopologyId", out _);

    private static bool SetPos(nint hwnd, nint after) => Native.SetWindowPos(hwnd, after, 0, 0, 0, 0, ZposFlags);

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != 0) Native.UnhookWinEvent(_hook);
        _probe.Dispose();
    }
}
