using System.Runtime.InteropServices;
using System.Windows.Input;

namespace TanukiBCL.Client;

// Poll only the four user-selected shortcuts. No keyboard text is captured or logged.
internal sealed class GlobalHotkeyMonitor(
    Action<bool> onPushToTalk,
    Action onRadio,
    Action onMute,
    Action onDeafen,
    Func<bool> suspended)
{
    private readonly object bindingsGate = new();
    private BindingSet bindings = new(0, 0, 0, 0, 0);
    private int bindingVersion;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public void UpdateBindings(ClientSettings settings)
    {
        TryResolve(settings.PushToTalkShortcut, out var pushToTalk);
        TryResolve(settings.ImpostorRadioShortcut, out var radio);
        TryResolve(settings.MuteShortcut, out var mute);
        TryResolve(settings.DeafenShortcut, out var deafen);
        lock (bindingsGate)
        {
            bindings = new BindingSet(pushToTalk, radio, mute, deafen, ++bindingVersion);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var previous = new PressedSet(false, false, false, false);
        var observedVersion = -1;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                BindingSet current;
                lock (bindingsGate) current = bindings;
                if (current.Version != observedVersion)
                {
                    onPushToTalk(false);
                    previous = new PressedSet(false, false, false, false);
                    observedVersion = current.Version;
                }

                var paused = suspended();
                var pressed = paused
                    ? new PressedSet(false, false, false, false)
                    : new PressedSet(IsPressed(current.PushToTalk), IsPressed(current.Radio),
                        IsPressed(current.Mute), IsPressed(current.Deafen));
                if (pressed.PushToTalk != previous.PushToTalk) onPushToTalk(pressed.PushToTalk);
                if (pressed.Radio && !previous.Radio) onRadio();
                if (pressed.Mute && !previous.Mute) onMute();
                if (pressed.Deafen && !previous.Deafen) onDeafen();
                previous = pressed;
                await Task.Delay(20, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            onPushToTalk(false);
        }
    }

    private static bool IsPressed(int virtualKey) => virtualKey != 0 &&
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static string NormalizeShortcut(string? value, string fallback) =>
        value is not null && TryResolve(value, out _) ? value : fallback;

    public static bool TryResolve(string? value, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var key = value.Trim();
        if (key.Length == 1)
        {
            var character = char.ToUpperInvariant(key[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }

        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var functionKey) && functionKey is >= 1 and <= 24)
        {
            virtualKey = 0x70 + functionKey - 1;
            return true;
        }

        if (key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = key[6..];
            if (suffix.Length == 1 && suffix[0] is >= '0' and <= '9')
            {
                virtualKey = 0x60 + suffix[0] - '0';
                return true;
            }
            virtualKey = suffix.ToLowerInvariant() switch
            {
                "multiply" => 0x6A, "add" => 0x6B, "subtract" => 0x6D,
                "decimal" => 0x6E, "divide" => 0x6F, _ => 0
            };
            return virtualKey != 0;
        }

        virtualKey = key.ToLowerInvariant() switch
        {
            "capslock" => 0x14, "space" => 0x20, "backspace" => 0x08,
            "delete" => 0x2E, "enter" => 0x0D, "up" => 0x26,
            "down" => 0x28, "left" => 0x25, "right" => 0x27,
            "home" => 0x24, "end" => 0x23, "pageup" => 0x21,
            "pagedown" => 0x22, "lshift" => 0xA0, "rshift" => 0xA1,
            "lalt" => 0xA4, "ralt" => 0xA5,
            "lcontrol" => 0xA2, "rcontrol" => 0xA3,
            "mousebutton4" => 0x05, "mousebutton5" => 0x06,
            _ => 0
        };
        return virtualKey != 0;
    }

    public static string? FromKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return $"Numpad{(int)key - (int)Key.NumPad0}";
        if (key is >= Key.F1 and <= Key.F24) return key.ToString();
        return key switch
        {
            Key.Escape => "Disabled", Key.Space => "Space", Key.Back => "Backspace",
            Key.Delete => "Delete", Key.Return => "Enter", Key.Up => "Up", Key.Down => "Down",
            Key.Left => "Left", Key.Right => "Right", Key.Home => "Home", Key.End => "End",
            Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.CapsLock => "CapsLock",
            Key.LeftShift => "LShift", Key.RightShift => "RShift",
            Key.LeftAlt => "LAlt", Key.RightAlt => "RAlt",
            Key.LeftCtrl => "LControl", Key.RightCtrl => "RControl",
            Key.Multiply => "NumpadMultiply", Key.Add => "NumpadAdd",
            Key.Subtract => "NumpadSubtract", Key.Decimal => "NumpadDecimal",
            Key.Divide => "NumpadDivide", _ => null
        };
    }

    private sealed record BindingSet(int PushToTalk, int Radio, int Mute, int Deafen, int Version);
    private sealed record PressedSet(bool PushToTalk, bool Radio, bool Mute, bool Deafen);
}
