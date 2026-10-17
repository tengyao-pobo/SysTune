using System.Windows;
using SysTune.App.Services;
using SysTune.Core.Settings;

namespace SysTune.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _app;
    private GlassMode _effective = GlassMode.Acrylic;

    internal SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => _app.ApplyGlass();
        SizeChanged += (_, _) => WindowEffects.UpdateRegion(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
        };
    }

    internal void ApplyGlass(GlassMode mode, bool dark)
    {
        Resources["WindowCorner"] = new CornerRadius(WindowEffects.IsWindows11 ? WindowEffects.CornerRadiusDip : 0);
        _effective = WindowEffects.Apply(this, mode, dark);
        UpdateTint();
    }

    internal void UpdateTint() => Tint.Opacity = _effective == GlassMode.Off ? 0.92 : Math.Max(0.6, _app.Settings.TintOpacity);

    internal void FocusSpotifySection()
    {
        Dispatcher.BeginInvoke(() => SpotifySection.BringIntoView(new Rect(0, 0, 1, 400)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
