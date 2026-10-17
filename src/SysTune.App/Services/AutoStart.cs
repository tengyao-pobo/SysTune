using Microsoft.Win32;
using SysTune.Core.Logging;

namespace SysTune.App.Services;

/// <summary>Per-user "start with Windows" via HKCU\...\Run (no elevation required).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SysTune";

    public static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Applies the setting; also repairs the path if the app was moved.</summary>
    public static void Apply(bool enabled, ILog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                if (!string.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                    log.Info("Autostart enabled");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log.Info("Autostart disabled");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.Warn("Could not update autostart registry value", ex);
        }
    }
}
