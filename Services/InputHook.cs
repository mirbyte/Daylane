using System.Diagnostics;
using System.Runtime.InteropServices;
using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// Low-level input hooks on a dedicated thread with its own message pump.
/// LL hooks are invoked on the installing thread; keeping them off the UI thread
/// avoids system-wide mouse stutter when Avalonia is busy.
/// </summary>
internal sealed class InputHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;

    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int WmLbuttondown = 0x0201;
    private const int WmRbuttondown = 0x0204;
    private const int WmMbuttondown = 0x0207;

    private readonly Action<InputEvent> _onInput;
    private readonly HashSet<uint> _keysDown = new();
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private Exception? _installError;
    private bool _disposed;

    public InputHook(Action<InputEvent> onInput)
    {
        _onInput = onInput;
        // Keep delegates rooted for the lifetime of the hook.
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "Daylane.InputHook"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _ready.Wait();
        if (_installError is not null)
        {
            _thread.Join(2000);
            _thread = null;
            throw new InvalidOperationException("Failed to install one or more input hooks.", _installError);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(5000);
        _thread = null;
        _ready.Dispose();
    }

    private void HookThreadMain()
    {
        _threadId = GetCurrentThreadId();

        try
        {
            using Process currentProcess = Process.GetProcessById(Environment.ProcessId);
            using ProcessModule? mainModule = currentProcess.MainModule;
            IntPtr hModule = GetModuleHandle(mainModule?.ModuleName);

            _keyboardHookId = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, hModule, 0);
            _mouseHookId = SetWindowsHookEx(WhMouseLl, _mouseProc, hModule, 0);

            if (_keyboardHookId == IntPtr.Zero || _mouseHookId == IntPtr.Zero)
            {
                UninstallHooks();
                throw new InvalidOperationException("SetWindowsHookEx returned null.");
            }
        }
        catch (Exception ex)
        {
            _installError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UninstallHooks();
    }

    private void UninstallHooks()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }

        _keysDown.Clear();
    }

    // Count only the first key-down for each physical press. Held-key auto-repeat
    // still delivers WM_KEYDOWN, so track which vkCodes are already down.
    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WmKeydown or WmSyskeydown or WmKeyup or WmSyskeyup)
            {
                uint vkCode = (uint)Marshal.ReadInt32(lParam);
                if (msg is WmKeydown or WmSyskeydown)
                {
                    if (_keysDown.Add(vkCode))
                    {
                        _onInput(new InputEvent(DateTime.UtcNow, "Key", 0, 0));
                    }
                }
                else
                {
                    _keysDown.Remove(vkCode);
                }
            }
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Keep this path allocation-free and lock-free: every mouse move hits here.
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WmLbuttondown or WmRbuttondown or WmMbuttondown)
            {
                _onInput(new InputEvent(DateTime.UtcNow, "Mouse", 0, 0));
            }
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
