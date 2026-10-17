using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SysTune.App.Interop;
using SysTune.App.Services;
using SysTune.Core.Logging;
using SysTune.App.ViewModels;
using SysTune.Core.Settings;

namespace SysTune.App.Views;

public partial class MainWindow : Window
{
    private const double ExpandedBaseWidth = AppSettings.ExpandedBaseWidth;
    private const double CompactBaseWidth = AppSettings.CompactBaseWidth;
    private readonly AppController _app;
    private readonly DispatcherTimer _saveDebounce;
    private bool _initialized;

    internal MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        _saveDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SavePlacement();
        };

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) =>
        {
            if (!_initialized) return;
            _saveDebounce.Stop();
            _saveDebounce.Start();
        };
        Root.MouseLeftButtonDown += OnRootMouseDown;
        PreviewKeyDown += OnKeyDown;

        // Slider handles mouse-down itself (IsMoveToPointEnabled), so listen to handled events too.
        SeekBar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => BeginSeek()), handledEventsToo: true);
        SeekBar.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) => await CommitSeekAsync()), handledEventsToo: true);
        SeekBar.AddHandler(LostMouseCaptureEvent, new MouseEventHandler(async (_, _) =>
        {
            if (!SeekBar.IsMouseCaptureWithin) await CommitSeekAsync();
        }), handledEventsToo: true);
        SeekBar.AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (IsRangeKey(e.Key)) BeginSeek();
        }), handledEventsToo: true);
        SeekBar.AddHandler(KeyUpEvent, new KeyEventHandler(async (_, e) =>
        {
            if (IsRangeKey(e.Key)) await CommitSeekAsync();
        }), handledEventsToo: true);

        VolumeSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _volumeByUser = true), handledEventsToo: true);
        VolumeSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _volumeByUser = false), handledEventsToo: true);
        VolumeSlider.AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) => _volumeByUser = IsRangeKey(e.Key)), handledEventsToo: true);
        VolumeSlider.AddHandler(KeyUpEvent, new KeyEventHandler((_, _) => _volumeByUser = false), handledEventsToo: true);
        VolumeSlider.PreviewMouseWheel += (_, e) =>
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 5 : -5), 0, 100);
            Vm?.Player.SetVolume(VolumeSlider.Value);
            e.Handled = true;
        };
        DpiChanged += (_, _) => _app.ApplyGlass();
        Deactivated += (_, _) =>
        {
            if (!_peeking) return;
            _peeking = false;
            SendToBottom();
        };
        // A desktop widget is never minimised (e.g. by "Minimize all" / Win+M).
        StateChanged += (_, _) =>
        {
            if (WindowState == System.Windows.WindowState.Minimized) WindowState = System.Windows.WindowState.Normal;
        };
        SizeChanged += (_, _) => WindowEffects.UpdateRegion(this);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private bool IsCompactMode => _app.Settings.Mode == WidgetMode.Compact;

    private DesktopLayer? _desktopLayer;

    // While true the widget may come to the front (shown from the tray); it drops back on deactivation.
    private bool _peeking;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _desktopLayer = new DesktopLayer(hwnd, _app.Log);
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        Closed += (_, _) => _desktopLayer?.Dispose();
        ApplyWindowSettings();
        ApplyModeLayout();
        _app.ApplyGlass();
        var state = IsCompactMode ? _app.Settings.Compact : _app.Settings.Expanded;
        if (!WindowPlacement.TryRestore(this, state)) WindowPlacement.PlaceDefault(this);
        _initialized = true;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Desktop layer: app windows always cover the widget; only DesktopLayer itself re-orders it.
        if (msg == Native.WM_WINDOWPOSCHANGING && !_peeking) _desktopLayer?.FilterWindowPosChanging(lParam);
        return 0;
    }

    /// <summary>Brings the widget to the front until the user switches to another window.</summary>
    internal void Peek()
    {
        if (_app.Settings.AlwaysOnTop) return;
        _peeking = true;
        Activate();
    }

    /// <summary>Moves the widget to the bottom of the z-order (desktop layer).</summary>
    internal void SendToBottom()
    {
        if (_desktopLayer is { IsShowingDesktop: false }) _desktopLayer.SendToBottom();
    }

    /// <summary>Applies the backdrop and returns the effective mode.</summary>
    internal GlassMode ApplyGlass(GlassMode mode, bool dark)
    {
        Resources["WindowCorner"] = new CornerRadius(WindowEffects.IsWindows11 ? WindowEffects.CornerRadiusDip : 0);
        return WindowEffects.Apply(this, mode, dark);
    }

    internal void ApplyWindowSettings()
    {
        var s = _app.Settings;
        // The desktop-layer z-order filter must be off while WPF changes Topmost, and back on afterwards.
        if (s.AlwaysOnTop)
        {
            if (_desktopLayer is not null) _desktopLayer.Enabled = false;
            Topmost = true;
        }
        else
        {
            Topmost = false;
            if (_desktopLayer is not null) _desktopLayer.Enabled = true;
        }
        // WPF's ShowInTaskbar=False gives the window a hidden owner, which constrains its z-order;
        // the taskbar button is controlled through extended styles instead.
        if (WindowEffects.SetToolWindow(this, !s.ShowInTaskbar) && IsVisible)
        {
            // The shell only re-reads taskbar styles when the window is shown again.
            Hide();
            Show();
        }
        SendToBottom();
        ResizeGrip.Visibility = s.LockPosition ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Switches between expanded and compact layouts, restoring each mode's own position.</summary>
    internal void ApplyMode()
    {
        ApplyModeLayout();
        var state = IsCompactMode ? _app.Settings.Compact : _app.Settings.Expanded;
        WindowPlacement.TryRestore(this, state);
    }

    private void ApplyModeLayout()
    {
        var s = _app.Settings;
        var baseWidth = IsCompactMode ? CompactBaseWidth : ExpandedBaseWidth;
        var width = IsCompactMode ? s.Compact.Width : s.Expanded.Width;
        Width = width;
        var scale = width / baseWidth;
        Zoom.ScaleX = scale;
        Zoom.ScaleY = scale;
    }

    internal void SavePlacement()
    {
        if (!_initialized) return;
        var state = WindowPlacement.Capture(this, Width);
        if (state is not null) _app.SavePlacement(state, _app.Settings.Mode);
    }

    internal void ResetPosition()
    {
        WindowPlacement.PlaceDefault(this);
        SavePlacement();
    }

    // ---------------- Interaction ----------------

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_app.Settings.LockPosition || e.ButtonState != MouseButtonState.Pressed) return;
        // Clicks inside popups (volume, devices) bubble through the logical tree; don't move the window for them.
        if (e.OriginalSource is not Visual source || PresentationSource.FromVisual(source) != PresentationSource.FromVisual(this)) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // mouse released before DragMove started
        }
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var baseWidth = IsCompactMode ? CompactBaseWidth : ExpandedBaseWidth;
        var min = baseWidth * AppSettings.MinScale;
        var max = baseWidth * AppSettings.MaxScale;
        // Diagonal drag: use the larger of the two deltas so the grip feels natural.
        var delta = Math.Abs(e.HorizontalChange) >= Math.Abs(e.VerticalChange) ? e.HorizontalChange : e.VerticalChange * (baseWidth / ActualHeight);
        var width = Math.Clamp(Width + delta, min, max);
        Width = width;
        Zoom.ScaleX = Zoom.ScaleY = width / baseWidth;
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SavePlacement();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.OriginalSource is TextBox) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        // Mark handled synchronously (before any await) so the key doesn't also reach the focused control.
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _app.HideWidget();
                break;
            case Key.Space when e.OriginalSource is not ButtonBase and not Slider:
            case Key.MediaPlayPause:
                e.Handled = true;
                _ = vm.Player.PlayPauseCommand.ExecuteAsync(null);
                break;
            case Key.MediaNextTrack:
            case Key.Right when ctrl:
                e.Handled = true;
                _ = vm.Player.NextCommand.ExecuteAsync(null);
                break;
            case Key.MediaPreviousTrack:
            case Key.Left when ctrl:
                e.Handled = true;
                _ = vm.Player.PreviousCommand.ExecuteAsync(null);
                break;
            case Key.OemComma when ctrl:
                e.Handled = true;
                _app.OpenSettings();
                break;
        }
    }

    // ---------------- Seek / volume ----------------

    private static bool IsRangeKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    private void BeginSeek()
    {
        if (Vm is { } vm && SeekBar.IsEnabled) vm.Player.IsSeeking = true;
    }

    private async Task CommitSeekAsync()
    {
        if (Vm is not { } vm || !vm.Player.IsSeeking) return;
        vm.Player.IsSeeking = false;
        await vm.Player.SeekToAsync(SeekBar.Value);
    }

    // Only user-initiated changes are sent to Spotify; poll-driven updates must not echo back.
    private bool _volumeByUser;

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeByUser || VolumeSlider.IsMouseCaptureWithin) Vm?.Player.SetVolume(e.NewValue);
    }

    private void VolumePopup_Closed(object? sender, EventArgs e)
    {
        _volumeByUser = false;
        VolumeToggle.IsChecked = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides to the notification area; only the tray "Exit" quits.
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            _app.HideWidget();
        }
        base.OnClosing(e);
    }
}
