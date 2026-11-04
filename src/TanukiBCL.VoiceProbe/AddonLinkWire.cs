using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TanukiBCL.Link;

public sealed class AddonLinkFramer
{
    public const int Maximum = 131072;
    private readonly byte[] pending = new byte[Maximum];
    private int length;
    public List<string> Push(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<string>();
        foreach (byte value in bytes)
        {
            if (value == 10) { if (frames.Count >= 64 || length >= Maximum) throw new InvalidOperationException("Too many or oversized addon link frames"); frames.Add(new UTF8Encoding(false, true).GetString(pending, 0, length)); length = 0; }
            else { if (length >= Maximum) throw new InvalidOperationException("Addon link frame too large"); pending[length++] = value; }
        }
        if (frames.Count > 64) throw new InvalidOperationException("Too many addon link frames");
        return frames;
    }
}

public static class AddonLinkWire
{
    public static bool Hex(string? value)
    {
        if (value == null || value.Length != 32) return false;
        foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
    public static string? Text(JsonElement data, string key) => data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool Integer(JsonElement data, string key, out long value)
    { value = 0; return data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value); }
    public static bool Valid(JsonElement message, int pid, string token, long now, ref long sequence)
    {
        if (message.ValueKind != JsonValueKind.Object || !Integer(message, "protocol", out long protocol) || protocol != 1 ||
            !Integer(message, "pid", out long target) || target != pid || Text(message, "session") != token ||
            !Integer(message, "sequence", out long seq) || seq <= sequence || seq > 9007199254740991L ||
            !Integer(message, "unixMs", out long time) || time <= 0 || time > 9007199254740991L || now - time > 500 || time - now > 100) return false;
        sequence = seq; return true;
    }
}
