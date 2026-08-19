using System.Diagnostics;
using System.Runtime.InteropServices;
using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// Keyboard LL hook plus mouse Raw Input on a dedicated thread with its own message pump.
/// Mouse uses Raw Input (RIDEV_INPUTSINK) so click counts do not sit in the global LL hook chain.
/// The hook thread is time-critical and the process opts out of execution-speed throttling
/// so Win11 EcoQoS cannot delay input capture while the app is in the tray.
/// </summary>
internal sealed class InputHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmQuit = 0x0012;
    private const uint WmInput = 0x00FF;

    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;

    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageGenericMouse = 0x02;
    private const ushort RiMouseLeftButtonDown = 0x0001;
    private const ushort RiMouseRightButtonDown = 0x0004;
    private const ushort RiMouseMiddleButtonDown = 0x0010;

    private const int ErrorClassAlreadyExists = 1410;
    private const int ThreadPriorityTimeCritical = 15;
    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private static readonly IntPtr HwndMessage = new(-3);

    private const string RawInputClassName = "Daylane.RawInput";

    private readonly Action<InputEvent> _onInput;
    private readonly HashSet<uint> _keysDown = new();
    private readonly LowLevelProc _keyboardProc;
    private readonly WndProc _wndProc;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private IntPtr _rawInputHwnd = IntPtr.Zero;
    private IntPtr _rawInputBuffer = IntPtr.Zero;
    private uint _rawInputBufferSize;
    private bool _mouseRawInputRegistered;
    private Exception? _installError;
    private bool _disposed;

    public InputHook(Action<InputEvent> onInput)
    {
        _onInput = onInput;
        // Keep delegates rooted for the lifetime of the hook.
        _keyboardProc = KeyboardCallback;
        _wndProc = RawInputWndProc;
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
            throw new InvalidOperationException("Failed to install input capture.", _installError);
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
        TryDisableExecutionSpeedThrottling();
        SetThreadPriority(GetCurrentThread(), ThreadPriorityTimeCritical);

        try
        {
            _rawInputBufferSize = 256;
            _rawInputBuffer = Marshal.AllocHGlobal((int)_rawInputBufferSize);

            using Process currentProcess = Process.GetProcessById(Environment.ProcessId);
            using ProcessModule? mainModule = currentProcess.MainModule;
            IntPtr hModule = GetModuleHandle(mainModule?.ModuleName);

            var wndClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hModule,
                lpszClassName = RawInputClassName
            };
            if (RegisterClass(ref wndClass) == 0
                && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException("RegisterClass returned null.");
            }

            _rawInputHwnd = CreateWindowEx(
                0,
                RawInputClassName,
                null,
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                IntPtr.Zero,
                hModule,
                IntPtr.Zero);
            if (_rawInputHwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateWindowEx returned null.");
            }

            var mouseDevice = new RAWINPUTDEVICE
            {
                usUsagePage = HidUsagePageGeneric,
                usUsage = HidUsageGenericMouse,
                dwFlags = RidevInputSink,
                hwndTarget = _rawInputHwnd
            };
            if (!RegisterRawInputDevices(ref mouseDevice, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                throw new InvalidOperationException("RegisterRawInputDevices failed.");
            }

            _mouseRawInputRegistered = true;

            _keyboardHookId = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, hModule, 0);
            if (_keyboardHookId == IntPtr.Zero)
            {
                throw new InvalidOperationException("SetWindowsHookEx returned null.");
            }
        }
        catch (Exception ex)
        {
            UninstallCapture();
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

        UninstallCapture();
    }

    private void UninstallCapture()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        if (_mouseRawInputRegistered)
        {
            var mouseDevice = new RAWINPUTDEVICE
            {
                usUsagePage = HidUsagePageGeneric,
                usUsage = HidUsageGenericMouse,
                dwFlags = RidevRemove,
                hwndTarget = IntPtr.Zero
            };
            RegisterRawInputDevices(ref mouseDevice, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            _mouseRawInputRegistered = false;
        }

        if (_rawInputHwnd != IntPtr.Zero)
        {
            DestroyWindow(_rawInputHwnd);
            _rawInputHwnd = IntPtr.Zero;
        }

        if (_rawInputBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_rawInputBuffer);
            _rawInputBuffer = IntPtr.Zero;
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

    private IntPtr RawInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInput)
        {
            HandleMouseRawInput(lParam);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleMouseRawInput(IntPtr hRawInput)
    {
        uint size = _rawInputBufferSize;
        uint copied = GetRawInputData(
            hRawInput,
            RidInput,
            _rawInputBuffer,
            ref size,
            (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (copied == uint.MaxValue || _rawInputBuffer == IntPtr.Zero)
        {
            return;
        }

        var raw = Marshal.PtrToStructure<RAWINPUT>(_rawInputBuffer);
        if (raw.header.dwType != RimTypeMouse)
        {
            return;
        }

        ushort flags = raw.mouse.usButtonFlags;
        if ((flags & RiMouseLeftButtonDown) != 0)
        {
            _onInput(new InputEvent(DateTime.UtcNow, "Mouse", 0, 0));
        }

        if ((flags & RiMouseRightButtonDown) != 0)
        {
            _onInput(new InputEvent(DateTime.UtcNow, "Mouse", 0, 0));
        }

        if ((flags & RiMouseMiddleButtonDown) != 0)
        {
            _onInput(new InputEvent(DateTime.UtcNow, "Mouse", 0, 0));
        }
    }

    private static void TryDisableExecutionSpeedThrottling()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingExecutionSpeed,
            StateMask = 0
        };
        SetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottling,
            in state,
            (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string? lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        ref RAWINPUTDEVICE pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        in PROCESS_POWER_THROTTLING_STATE processInformation,
        uint processInformationSize);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public uint ulButtons;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }
}
