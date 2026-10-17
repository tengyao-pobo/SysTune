using SysTune.Core.Settings;

namespace SysTune.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("systune-settings");

    private string PathFor(string name) => Path.Combine(_dir.FullName, name);

    [Fact]
    public void RoundTrip_PersistsValues()
    {
        var path = PathFor("settings.json");
        using (var store = new SettingsStore(path))
        {
            var s = store.Load();
            s.Theme = ThemeMode.Light;
            s.TintOpacity = 0.8;
            s.Spotify.ClientId = "  abc123  ";
            s.Expanded.Left = 10; s.Expanded.Top = 20; s.Expanded.Right = 350; s.Expanded.Bottom = 600;
            store.Save();
        }
        using var reloaded = new SettingsStore(path);
        var r = reloaded.Load();
        Assert.Equal(ThemeMode.Light, r.Theme);
        Assert.Equal(0.8, r.TintOpacity);
        Assert.Equal("abc123", r.Spotify.ClientId);
        Assert.True(r.Expanded.HasPlacement);
        Assert.Contains("\"theme\": \"Light\"", File.ReadAllText(path));
    }

    [Fact]
    public void CorruptFile_IsBackedUpAndDefaultsUsed()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, "{ not json");
        using var store = new SettingsStore(path);
        var s = store.Load();
        Assert.Equal(ThemeMode.System, s.Theme);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var s = new AppSettings
        {
            TintOpacity = 5,
            RefreshIntervalMs = 1,
            Theme = (ThemeMode)99,
            Spotify = new SpotifySettings { CallbackPort = 80 },
            Expanded = new WindowState { Width = 10_000 },
            Compact = null!,
        };
        s.Normalize();
        Assert.Equal(1.0, s.TintOpacity);
        Assert.Equal(500, s.RefreshIntervalMs);
        Assert.Equal(ThemeMode.System, s.Theme);
        Assert.Equal(SpotifySettings.DefaultPort, s.Spotify.CallbackPort);
        Assert.Equal(560, s.Expanded.Width);
        Assert.Equal(460, s.Compact.Width);
        Assert.Equal("http://127.0.0.1:47821/callback", s.Spotify.RedirectUri);
    }

    [Fact]
    public async Task DebouncedSave_CoalescesAndFlushesOnDispose()
    {
        var path = PathFor("debounce.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Current.AlwaysOnTop = true;
        store.SaveDebounced(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.False(File.Exists(path));
        store.Dispose();
        Assert.Contains("\"alwaysOnTop\": true", File.ReadAllText(path));
    }

    public void Dispose() => _dir.Delete(true);
}
