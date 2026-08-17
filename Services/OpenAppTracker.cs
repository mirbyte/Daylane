using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// Polls visible top-level windows for apps that are open (not necessarily focused).
/// </summary>
internal sealed class OpenAppTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly Dictionary<uint, ForegroundApp> _pidCache = new();
    private IReadOnlyList<ForegroundApp> _current = [];
    private bool _disposed;

    public OpenAppTracker()
    {
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<IReadOnlyList<ForegroundApp>>? Changed;

    public IReadOnlyList<ForegroundApp> Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Start()
    {
        Poll();
        _timer.Change(PollInterval, PollInterval);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<ForegroundApp> sample = Sample();
        bool changed;

        lock (_lock)
        {
            changed = !SameSet(_current, sample);
            if (changed)
            {
                _current = sample;
            }
        }

        if (changed)
        {
            Changed?.Invoke(sample);
        }
    }

    private IReadOnlyList<ForegroundApp> Sample()
    {
        var byKey = new Dictionary<string, ForegroundApp>(StringComparer.OrdinalIgnoreCase);
        var seenPids = new HashSet<uint>();

        EnumWindows(
            (hwnd, _) =>
            {
                if (!IsCandidateWindow(hwnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId == 0 || processId == (uint)Environment.ProcessId)
                {
                    return true;
                }

                if (!seenPids.Add(processId))
                {
                    // Same process already resolved from another window.
                    return true;
                }

                ForegroundApp app = ResolveProcess(processId);
                if (app.ProcessName.Length == 0
                    || string.Equals(app.ProcessName, "Unknown", StringComparison.OrdinalIgnoreCase)
                    || ShouldIgnore(app))
                {
                    return true;
                }

                string key = IdentityKey(app);
                byKey.TryAdd(key, app);
                return true;
            },
            IntPtr.Zero);

        PrunePidCache(seenPids);
        return byKey.Values.ToList();
    }

    private static bool ShouldIgnore(ForegroundApp app)
    {
        if (string.Equals(app.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(app.ProcessName, "SystemSettings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            app.DisplayName,
            "Microsoft® Windows® Operating System",
            StringComparison.OrdinalIgnoreCase);
    }

    private ForegroundApp ResolveProcess(uint processId)
    {
        lock (_lock)
        {
            if (_pidCache.TryGetValue(processId, out ForegroundApp cached))
            {
                return cached;
            }
        }

        ForegroundApp resolved = ResolveProcessUncached(processId);
        lock (_lock)
        {
            _pidCache[processId] = resolved;
        }

        return resolved;
    }

    private void PrunePidCache(HashSet<uint> livePids)
    {
        lock (_lock)
        {
            if (_pidCache.Count == 0)
            {
                return;
            }

            var stale = _pidCache.Keys.Where(pid => !livePids.Contains(pid)).ToList();
            foreach (uint pid in stale)
            {
                _pidCache.Remove(pid);
            }
        }
    }

    private static ForegroundApp ResolveProcessUncached(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string processName = string.IsNullOrWhiteSpace(process.ProcessName)
                ? "Unknown"
                : process.ProcessName;

            string exePath = TryGetProcessPath(process) ?? "";
            string displayName = TryGetDisplayName(exePath, processName);
            return new ForegroundApp(processName, exePath, displayName, false);
        }
        catch (ArgumentException)
        {
            return ForegroundApp.Unknown;
        }
        catch (InvalidOperationException)
        {
            return ForegroundApp.Unknown;
        }
    }

    private static bool IsCandidateWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            return false;
        }

        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        if (GetWindowTextLength(hwnd) == 0)
        {
            return false;
        }

        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        if ((exStyle & WsExToolWindow) != 0 && (exStyle & WsExAppWindow) == 0)
        {
            return false;
        }

        return true;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            // Elevated / protected processes often deny MainModule access.
        }

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return buffer.ToString();
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return null;
    }

    private static string TryGetDisplayName(string exePath, string processName)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return processName;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription;
            }

            if (!string.IsNullOrWhiteSpace(info.ProductName))
            {
                return info.ProductName;
            }
        }
        catch (Exception)
        {
            // Fall through to file name.
        }

        try
        {
            return Path.GetFileNameWithoutExtension(exePath);
        }
        catch (Exception)
        {
            return processName;
        }
    }

    internal static string IdentityKey(ForegroundApp app) =>
        string.IsNullOrEmpty(app.ExePath) ? app.ProcessName : app.ExePath;

    private static bool SameSet(IReadOnlyList<ForegroundApp> a, IReadOnlyList<ForegroundApp> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var keys = new HashSet<string>(a.Select(IdentityKey), StringComparer.OrdinalIgnoreCase);
        return b.All(app => keys.Contains(IdentityKey(app)));
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        StringBuilder lpExeName,
        ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
