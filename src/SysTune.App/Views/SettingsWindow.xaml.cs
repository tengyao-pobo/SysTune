using System.Windows;
using SysTune.App.Services;
using SysTune.App.ViewModels;
using SysTune.Core.Settings;

namespace SysTune.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _app;

    internal SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => _app.ApplyGlass();
        DataContextChanged += (_, _) => UpdateStatusDot();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
        };
    }

    internal void ApplyGlass(GlassMode mode, bool dark)
    {
        Resources["WindowCorner"] = new CornerRadius(WindowEffects.IsWindows11 ? 8 : 0);
        var effective = WindowEffects.Apply(this, mode, dark);
        Tint.Opacity = effective == GlassMode.Off ? 1.0 : Math.Max(0.82, _app.Settings.TintOpacity);
    }

    internal void FocusSpotifySection()
    {
        Dispatcher.BeginInvoke(() => SpotifySection.BringIntoView(new Rect(0, 0, 1, 400)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateStatusDot()
    {
        if (DataContext is not SettingsViewModel vm) return;
        void Update() => StatusDot.Fill = (System.Windows.Media.Brush)FindResource(vm.IsSpotifyConnected ? "Brush.Spotify" : "Brush.Text3");
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsSpotifyConnected)) Update();
        };
        Update();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
