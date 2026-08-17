using System.Globalization;
using System.Runtime.InteropServices;

namespace Daylane.Services;

/// <summary>
/// Idle detection via GetLastInputInfo (keyboard, mouse buttons, mouse move, wheel).
/// Same approach as the standalone Inactivity Timer: threshold-based Away sessions.
/// </summary>
internal static class IdleMonitor
{
    public const int DefaultThresholdMinutes = 15;
    public const int MinThresholdMinutes = 1;
    public const int MaxThresholdMinutes = 240;

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "config.ini");

    public static TimeSpan Threshold { get; private set; } = TimeSpan.FromMinutes(DefaultThresholdMinutes);

    public static void Load()
    {
        int minutes = DefaultThresholdMinutes;
        try
        {
            if (File.Exists(ConfigPath))
            {
                foreach (string raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("threshold_minutes", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = line.IndexOf('=');
                        if (eq >= 0
                            && int.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                            && parsed is >= MinThresholdMinutes and <= MaxThresholdMinutes)
                        {
                            minutes = parsed;
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // Keep default.
        }

        Threshold = TimeSpan.FromMinutes(minutes);
    }

    public static double GetIdleSeconds()
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        ulong idleMs = unchecked(GetTickCount64() - info.dwTime);
        return idleMs / 1000.0;
    }

    public static bool IsAway() => GetIdleSeconds() >= Threshold.TotalSeconds;

    public static DateTime LastInputUtc()
    {
        double idleSeconds = GetIdleSeconds();
        return DateTime.UtcNow - TimeSpan.FromSeconds(idleSeconds);
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
