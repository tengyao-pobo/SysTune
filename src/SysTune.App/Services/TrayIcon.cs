using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SysTune.App.Interop;
using Forms = System.Windows.Forms;

namespace SysTune.App.Services;

/// <summary>
/// Notification-area icon. Uses the WinForms NotifyIcon for the shell integration and a themed WPF
/// ContextMenu so the menu matches the widget's look.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _owner;
    private readonly Func<ContextMenu> _menuFactory;
    private System.Drawing.Icon? _current;
    private DateTime _lastClick;

    public TrayIcon(Window owner, Func<ContextMenu> menuFactory)
    {
        _owner = owner;
        _menuFactory = menuFactory;
        _icon = new Forms.NotifyIcon { Text = "SysTune", Visible = false };
        _icon.MouseUp += OnMouseUp;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        UpdateIcon();
        _icon.Visible = true;
    }

    public event EventHandler? LeftClick;

    /// <summary>Re-selects the icon to match the taskbar (light vs dark) theme.</summary>
    public void UpdateIcon()
    {
        var lightTaskbar = SystemUsesLightTaskbar();
        var uri = new Uri(lightTaskbar ? "pack://application:,,,/Assets/SysTune-tray-light.ico" : "pack://application:,,,/Assets/SysTune-tray-dark.ico");
        try
        {
            using var stream = Application.GetResourceStream(uri)!.Stream;
            var next = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            _icon.Icon = next;
            _current?.Dispose();
            _current = next;
        }
        catch (Exception)
        {
            _icon.Icon ??= System.Drawing.SystemIcons.Application;
        }
    }

    /// <summary>Sets the hover tooltip (max 127 chars).</summary>
    public void SetTooltip(string text) => _icon.Text = text.Length > 127 ? text[..127] : text;

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            // NotifyIcon raises MouseUp for each click of a double-click; debounce.
            if ((DateTime.UtcNow - _lastClick).TotalMilliseconds < 250) return;
            _lastClick = DateTime.UtcNow;
            LeftClick?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Button == Forms.MouseButtons.Right)
        {
            var menu = _menuFactory();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            // The menu only closes on outside clicks / takes keyboard input if its own popup window is foreground.
            // (The widget may be hidden, so its HWND can't be used for this.)
            menu.Opened += (_, _) =>
            {
                if (PresentationSource.FromVisual(menu) is HwndSource source) Native.SetForegroundWindow(source.Handle);
            };
            menu.IsOpen = true;
        }
    }

    // The taskbar theme can change independently of the app theme.
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        => _owner.Dispatcher.BeginInvoke(UpdateIcon);

    private static bool SystemUsesLightTaskbar()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
