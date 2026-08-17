using Microsoft.Win32;

namespace Daylane.Services;

internal static class StartupRegistration
{
    public const string TrayArgument = "--tray";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Daylane";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve Daylane executable path.");
            key.SetValue(ValueName, $"\"{exe}\" {TrayArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// If autostart is on, rewrite the Run value so a moved portable folder still launches.
    /// </summary>
    public static void RefreshRegisteredPathIfEnabled()
    {
        if (IsEnabled())
        {
            SetEnabled(true);
        }
    }

    public static bool HasTrayArgument(IEnumerable<string> args)
        => args.Any(a => string.Equals(a, TrayArgument, StringComparison.OrdinalIgnoreCase));
}
