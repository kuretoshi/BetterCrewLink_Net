using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BetterCrewLinkKai.DotNet;

internal static class OverlayWindowInterop
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const int WsVisible = 0x10000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTop = new(0);
    private static readonly nint HwndTopmost = new(-1);
    private static readonly Dictionary<nint, nint> AttachedTargets = [];

    public static void ApplyOverlayStyle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, style | WsExTransparent | WsExLayered | WsExToolWindow);
    }

    public static void AttachToAmongUs(Window window)
    {
        var overlayHandle = new WindowInteropHelper(window).Handle;
        var targetHandle = FindAmongUsWindow();
        if (overlayHandle == 0 || targetHandle == 0)
        {
            return;
        }

        if (AttachedTargets.TryGetValue(overlayHandle, out var attachedTarget) &&
            attachedTarget == targetHandle)
        {
            return;
        }

        SetWindowLongPtr(overlayHandle, GwlpHwndParent, targetHandle);
        var targetThreadId = GetWindowThreadProcessId(targetHandle, out _);
        var overlayThreadId = GetWindowThreadProcessId(overlayHandle, out _);
        if (targetThreadId != 0 && overlayThreadId != 0)
        {
            AttachThreadInput(targetThreadId, overlayThreadId, false);
        }

        SetWindowPos(overlayHandle, targetHandle, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        SetWindowPos(targetHandle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        RestoreLayeredTransparency(overlayHandle);
        AttachedTargets[overlayHandle] = targetHandle;
    }

    public static void ActivateOverlay(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0)
        {
            SetForegroundWindow(handle);
        }
    }

    public static void KeepTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public static void KeepAttached(Window window)
    {
        AttachToAmongUs(window);
    }

    private static void RestoreLayeredTransparency(nint overlayHandle)
    {
        var style = GetWindowLongPtr(overlayHandle, GwlStyle);
        SetWindowLongPtr(overlayHandle, GwlStyle, style | WsVisible);

        var exStyle = GetWindowLongPtr(overlayHandle, GwlExStyle);
        SetWindowLongPtr(overlayHandle, GwlExStyle, exStyle & ~WsExLayered);
        SetWindowLongPtr(overlayHandle, GwlExStyle, exStyle | WsExTransparent | WsExLayered | WsExToolWindow);
        SetWindowLongPtr(overlayHandle, GwlStyle, style);
    }

    private static nint FindAmongUsWindow()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Among Us"))
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle != 0 && IsWindowVisible(handle))
                {
                    return handle;
                }
            }
            catch
            {
                // Keep looking; the process may exit while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        return 0;
    }

    private static nint GetWindowLongPtr(nint hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : GetWindowLong32(hwnd, index);
    }

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : SetWindowLong32(hwnd, index, value);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern nint GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern nint SetWindowLong32(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
