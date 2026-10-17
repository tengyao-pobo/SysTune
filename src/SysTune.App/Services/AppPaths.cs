namespace SysTune.App.Services;

/// <summary>Per-user storage locations. Roaming data = settings/tokens, local = logs.</summary>
internal static class AppPaths
{
    public static string RoamingDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysTune");

    public static string LocalDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysTune");

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");

    public static string TokenFile => Path.Combine(LocalDir, "spotify.token");

    public static string LogDir => Path.Combine(LocalDir, "logs");

    public static string LogFile => Path.Combine(LogDir, "systune.log");
}
