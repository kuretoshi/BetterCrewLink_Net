using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class HotkeyEventArgs : EventArgs
{
    public HotkeyEventArgs(string action, bool isPressed)
    {
        Action = action;
        IsPressed = isPressed;
    }

    public string Action { get; }

    public bool IsPressed { get; }
}

public sealed class HotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;

    private readonly LowLevelKeyboardProc keyboardProc;
    private readonly LowLevelMouseProc mouseProc;
    private readonly Dictionary<Key, string> actionsByKey = [];
    private readonly Dictionary<int, string> actionsByMouseButton = [];
    private readonly HashSet<Key> pressedKeys = [];
    private readonly HashSet<int> pressedMouseButtons = [];
    private nint keyboardHook;
    private nint mouseHook;
    private bool disposed;

    public HotkeyService()
    {
        keyboardProc = HookCallback;
        mouseProc = MouseHookCallback;
    }

    public event EventHandler<HotkeyEventArgs>? HotkeyChanged;

    public void Start()
    {
        if (keyboardHook != 0 && mouseHook != 0)
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = GetModuleHandle(currentModule?.ModuleName);
        if (keyboardHook == 0)
        {
            keyboardHook = SetWindowsHookEx(WhKeyboardLl, keyboardProc, moduleHandle, 0);
        }

        if (mouseHook == 0)
        {
            mouseHook = SetWindowsMouseHookEx(WhMouseLl, mouseProc, moduleHandle, 0);
        }
    }

    public void Configure(string pushToTalk, string mute, string deafen, string impostorRadio)
    {
        actionsByKey.Clear();
        actionsByMouseButton.Clear();
        Add(pushToTalk, "push-to-talk");
        Add(mute, "mute");
        Add(deafen, "deafen");
        Add(impostorRadio, "impostor-radio");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (keyboardHook != 0)
        {
            UnhookWindowsHookEx(keyboardHook);
            keyboardHook = 0;
        }

        if (mouseHook != 0)
        {
            UnhookWindowsHookEx(mouseHook);
            mouseHook = 0;
        }
    }

    private void Add(string shortcut, string action)
    {
        if (TryParseMouseButton(shortcut, out var mouseButton))
        {
            actionsByMouseButton[mouseButton] = action;
            return;
        }

        if (TryParseKey(shortcut, out var key))
        {
            actionsByKey[key] = action;
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            var key = KeyInterop.KeyFromVirtualKey(Marshal.ReadInt32(lParam));
            if (actionsByKey.TryGetValue(key, out var action))
            {
                var isPressed = message is WmKeyDown or WmSysKeyDown;
                var isReleased = message is WmKeyUp or WmSysKeyUp;
                if (isPressed && pressedKeys.Add(key))
                {
                    HotkeyChanged?.Invoke(this, new HotkeyEventArgs(action, true));
                }
                else if (isReleased && pressedKeys.Remove(key))
                {
                    HotkeyChanged?.Invoke(this, new HotkeyEventArgs(action, false));
                }
            }
        }

        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            if (message is WmXButtonDown or WmXButtonUp)
            {
                var hookStruct = Marshal.PtrToStructure<MouseHookStruct>(lParam);
                var xButton = (hookStruct.MouseData >> 16) & 0xffff;
                var mouseButton = xButton + 3;
                if (actionsByMouseButton.TryGetValue(mouseButton, out var action))
                {
                    if (message == WmXButtonDown && pressedMouseButtons.Add(mouseButton))
                    {
                        HotkeyChanged?.Invoke(this, new HotkeyEventArgs(action, true));
                    }
                    else if (message == WmXButtonUp && pressedMouseButtons.Remove(mouseButton))
                    {
                        HotkeyChanged?.Invoke(this, new HotkeyEventArgs(action, false));
                    }
                }
            }
        }

        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    private static bool TryParseKey(string shortcut, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        var normalized = shortcut.Trim();
        normalized = normalized.Equals("RAlt", StringComparison.OrdinalIgnoreCase) ? "RightAlt" : normalized;
        normalized = normalized.Equals("LAlt", StringComparison.OrdinalIgnoreCase) ? "LeftAlt" : normalized;
        normalized = normalized.Equals("RControl", StringComparison.OrdinalIgnoreCase) ? "RightCtrl" : normalized;
        normalized = normalized.Equals("LControl", StringComparison.OrdinalIgnoreCase) ? "LeftCtrl" : normalized;
        normalized = normalized.Equals("RShift", StringComparison.OrdinalIgnoreCase) ? "RightShift" : normalized;
        normalized = normalized.Equals("LShift", StringComparison.OrdinalIgnoreCase) ? "LeftShift" : normalized;

        return Enum.TryParse(normalized, ignoreCase: true, out key) && key != Key.None;
    }

    private static bool TryParseMouseButton(string shortcut, out int button)
    {
        button = 0;
        return shortcut.StartsWith("MouseButton", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(shortcut["MouseButton".Length..], out button) &&
               button >= 4 &&
               button <= 5;
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseHookStruct
    {
        public readonly Point Point;
        public readonly int MouseData;
        public readonly int Flags;
        public readonly int Time;
        public readonly nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
    private static extern nint SetWindowsMouseHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
