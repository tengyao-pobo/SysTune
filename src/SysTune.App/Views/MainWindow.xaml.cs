using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SysTune.App.Services;
using SysTune.App.ViewModels;
using SysTune.Core.Settings;

namespace SysTune.App.Views;

public partial class MainWindow : Window
{
    private const double ExpandedBaseWidth = 340;
    private const double CompactBaseWidth = 460;
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
        DpiChanged += (_, _) => _app.ApplyGlass();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private bool IsCompactMode => _app.Settings.Mode == WidgetMode.Compact;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowSettings();
        ApplyModeLayout();
        _app.ApplyGlass();
        var state = IsCompactMode ? _app.Settings.Compact : _app.Settings.Expanded;
        if (!WindowPlacement.TryRestore(this, state)) WindowPlacement.PlaceDefault(this);
        _initialized = true;
    }

    /// <summary>Applies the backdrop and returns the effective mode.</summary>
    internal GlassMode ApplyGlass(GlassMode mode, bool dark)
    {
        Resources["WindowCorner"] = new CornerRadius(WindowEffects.IsWindows11 ? 8 : 0);
        return WindowEffects.Apply(this, mode, dark);
    }

    internal void ApplyWindowSettings()
    {
        var s = _app.Settings;
        Topmost = s.AlwaysOnTop;
        ShowInTaskbar = s.ShowInTaskbar;
        WindowEffects.SetToolWindow(this, !s.ShowInTaskbar);
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
        var min = baseWidth * 0.85;
        var max = baseWidth * 1.6;
        // Diagonal drag: use the larger of the two deltas so the grip feels natural.
        var delta = Math.Abs(e.HorizontalChange) >= Math.Abs(e.VerticalChange) ? e.HorizontalChange : e.VerticalChange * (baseWidth / ActualHeight);
        var width = Math.Clamp(Width + delta, min, max);
        Width = width;
        Zoom.ScaleX = Zoom.ScaleY = width / baseWidth;
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SavePlacement();

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Escape:
                _app.HideWidget();
                e.Handled = true;
                break;
            case Key.Space when e.OriginalSource is not ButtonBase:
                if (vm.Player.PlayPauseCommand.CanExecute(null)) await vm.Player.PlayPauseCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Key.MediaNextTrack or Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                await vm.Player.NextCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Key.MediaPreviousTrack or Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                await vm.Player.PreviousCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Key.OemComma when Keyboard.Modifiers == ModifierKeys.Control:
                _app.OpenSettings();
                e.Handled = true;
                break;
        }
    }

    // ---------------- Seek / volume ----------------

    private void SeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.Player.IsSeeking = true;
    }

    private async void SeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e) => await CommitSeekAsync();

    private async void SeekBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (Vm?.Player.IsSeeking == true) await CommitSeekAsync();
    }

    private async Task CommitSeekAsync()
    {
        if (Vm is not { } vm || !vm.Player.IsSeeking) return;
        vm.Player.IsSeeking = false;
        await vm.Player.SeekToAsync(SeekBar.Value);
    }

    // Only user-initiated changes are sent to Spotify; poll-driven updates must not echo back.
    private bool _volumeByUser;

    private void Volume_UserInput(object sender, RoutedEventArgs e) => _volumeByUser = true;

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeByUser) Vm?.Player.SetVolume(e.NewValue);
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
