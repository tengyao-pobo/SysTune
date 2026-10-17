using System.Windows;
using Microsoft.Win32;
using SysTune.Core.Settings;
using ThemeMode = SysTune.Core.Settings.ThemeMode;

namespace SysTune.App.Services;

/// <summary>Swaps the colour dictionary between dark/light and follows the Windows app theme in System mode.</summary>
internal sealed class ThemeService : IDisposable
{
    private ThemeMode _mode = ThemeMode.System;
    private bool _applied;

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public event EventHandler? ThemeChanged;

    public bool IsDark { get; private set; } = true;

    public void Apply(ThemeMode mode)
    {
        _mode = mode;
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemUsesLightTheme(),
        };
        var dicts = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var existing = dicts.FirstOrDefault(d => d.Source is not null && (d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.Ordinal) || d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.Ordinal)));
        if (_applied && existing is not null && IsDark == dark) return;
        _applied = true;
        var next = new ResourceDictionary { Source = uri };
        if (existing is not null) dicts[dicts.IndexOf(existing)] = next;
        else dicts.Insert(0, next);
        IsDark = dark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode != ThemeMode.System || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
