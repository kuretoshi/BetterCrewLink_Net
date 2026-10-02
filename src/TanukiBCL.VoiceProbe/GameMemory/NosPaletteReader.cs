using System.Diagnostics;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

internal sealed class NosPaletteReader
{
    private int pid = -1;
    private Task<Layout>? pending;
    private Layout? layout;
    private DateTimeOffset retryAt;
    public void Reset()
    {
        pid = -1;
        pending = null;
        layout = null;
        retryAt = default;
    }

    public string[]? Update(int processId, int targetPointerSize, Func<long, int, byte[]> read)
    {
        if (targetPointerSize != 8) { Reset(); return null; }
        if (pid != processId) { Reset(); pid = processId; }
        if (pending is { IsCompleted: true })
        {
            try { layout = pending.GetAwaiter().GetResult(); }
            catch { retryAt = DateTimeOffset.UtcNow.AddSeconds(30); }
            pending = null;
        }
        if (layout is null)
        {
            if (pending is null && DateTimeOffset.UtcNow >= retryAt)
                pending = ResolveAsync(processId, targetPointerSize);
            return null;
        }
        try { return Read(layout, read); }
        catch { return null; } // A changing palette must not publish a torn color array.
    }

    private static async Task<Layout> ResolveAsync(int pid, int targetPointerSize)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(
            NosSnapshotReader.FindHelper(targetPointerSize) ??
            throw new FileNotFoundException("NoS reader missing"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.StartInfo.ArgumentList.Add("palette");
        process.StartInfo.ArgumentList.Add(pid.ToString());
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var result = JsonSerializer.Deserialize<Response>(await output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            await error;
            if (process.ExitCode != 0 || result?.Status != "ok" || result.Pid != pid || result.Metadata is null)
                throw new InvalidDataException("NoS palette unavailable");
            result.Metadata.Validate(pid);
            if (result.Metadata.PointerSize != targetPointerSize)
                throw new InvalidDataException("NoS palette helper architecture mismatch");
            return result.Metadata;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("NoS palette helper timed out");
        }
    }

    internal static string[] Read(Layout layout, Func<long, int, byte[]> read)
    {
        layout.Validate(layout.Pid);
        long Pointer(long address) => checked((long)BitConverter.ToUInt64(read(address, 8)));
        var array = Pointer(layout.ArraySlot);
        if (!ValidPointer(array, layout.PointerSize) || Pointer(array) != layout.ArrayType ||
            BitConverter.ToInt32(read(array + layout.ArrayLengthOffset, 4)) != 32)
            throw new InvalidDataException("NoS palette changed");
        var bytes = read(array + layout.ArrayDataOffset, 32 * layout.Stride);
        var colors = Enumerable.Range(0, 32).Select(i =>
        {
            var values = new[] { layout.R, layout.G, layout.B }.Select(offset =>
                (double)BitConverter.ToSingle(bytes, i * layout.Stride + offset)).ToArray();
            if (values.Any(v => !double.IsFinite(v) || v < 0 || v > 1))
                throw new InvalidDataException("Invalid NoS palette color");
            return NosColor.ToHex(values[0], values[1], values[2])!;
        }).ToArray();
        if (Pointer(layout.ArraySlot) != array || Pointer(array) != layout.ArrayType ||
            !read(array + layout.ArrayDataOffset, bytes.Length).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("NoS palette changed during read");
        return colors;
    }

    private static bool ValidPointer(long value, int pointerSize) =>
        value >= 0x10000 && value % pointerSize == 0;
    private sealed record Response(string Status, int Pid, Layout? Metadata);
    internal sealed class Layout
    {
        public int Pid { get; set; }
        public int PointerSize { get; set; }
        public long ArraySlot { get; set; }
        public long ArrayType { get; set; }
        public int ArrayLengthOffset { get; set; }
        public int ArrayDataOffset { get; set; }
        public int Stride { get; set; }
        public int R { get; set; }
        public int G { get; set; }
        public int B { get; set; }
        public void Validate(int pid)
        {
            if (Pid != pid || PointerSize != 8 || !ValidPointer(ArraySlot, PointerSize) ||
                !ValidPointer(ArrayType, PointerSize) ||
                ArrayLengthOffset != 8 || ArrayDataOffset is < 8 or > 64 || Stride is < 12 or > 64 ||
                new[] { R, G, B }.Any(offset => offset < 0 || offset > Stride - 4))
                throw new InvalidDataException("Unsupported NoS palette");
        }
    }
}

public static class NosColor
{
    public static string? For(Player player) => player.NosLobbyColor ?? (player.NosPlayer is { } p
        ? ToHex(p.ColorR, p.ColorG, p.ColorB) : null);

    public static string? ToHex(double red, double green, double blue)
    {
        var values = new[] { red, green, blue };
        if (values.Any(v => !double.IsFinite(v))) return null;
        return "#" + string.Concat(values.Select(v => ((int)Math.Floor(Math.Clamp(v, 0, 1) * 255 + 0.5)).ToString("x2")));
    }
}
