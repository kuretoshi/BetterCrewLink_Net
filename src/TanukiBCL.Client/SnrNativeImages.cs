using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TanukiBCL.Client;

// One serialized worker keeps one bounded bundle index; decoded images remain
// in WPF's existing per-avatar lifetime. Failed requests never enter the cache.
internal static class SnrNativeImages
{
    internal sealed record Image(byte[] Png, bool? Adaptive, bool? SnrVisorLayout);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, Task<Image?>> Cache = [];
    private static readonly Dictionary<string, long> CachedAt = [];
    private static readonly SemaphoreSlim WorkerGate = new(1);
    private static Process? worker;
    private static long lastUse;
    private static TimeSpan requestTimeout = TimeSpan.FromSeconds(15);
    private static int testDelayMilliseconds;
    private static readonly Timer Idle = new(_ => CloseIdle(), null, 30000, 30000);

    internal static Task<Image?> GetAsync(string executable, string id, CosmeticPart part)
    {
        if (!Path.IsPathFullyQualified(executable) || id.Length > 1024 || !id.StartsWith("Modded_", StringComparison.Ordinal))
            return Task.FromResult<Image?>(null);
        var root = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var kind = part switch { CosmeticPart.Hat => "hat-front", CosmeticPart.HatBack => "hat-back", CosmeticPart.Visor => "visor", _ => "skin" };
        var key = JsonSerializer.Serialize(new { root, id, part = kind });
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var request) &&
                (!request.IsCompleted || Environment.TickCount64 - CachedAt.GetValueOrDefault(key) < 60000)) return request;
            if (Cache.Values.Count(value => !value.IsCompleted) >= 8) return Task.FromResult<Image?>(null);
            if (!Cache.ContainsKey(key) && Cache.Count >= 64)
            {
                var old = Cache.FirstOrDefault(value => value.Value.IsCompleted);
                if (old.Key is null) return Task.FromResult<Image?>(null);
                Cache.Remove(old.Key); CachedAt.Remove(old.Key);
            }
            CachedAt[key] = Environment.TickCount64;
            request = Task.Run(() => RequestAsync(key));
            Cache[key] = request;
            _ = request.ContinueWith(done =>
            {
                lock (CacheGate)
                {
                    if ((!done.IsCompletedSuccessfully || done.Result is null) && Cache.GetValueOrDefault(key) == done)
                    { Cache.Remove(key); CachedAt.Remove(key); }
                    while (Cache.Count > 64 || Cache.Values.Where(x => x.IsCompletedSuccessfully).Sum(x => (long)(x.Result?.Png.Length ?? 0)) > 32L * 1024 * 1024)
                    {
                        var old = Cache.FirstOrDefault(x => x.Value.IsCompleted);
                        if (old.Key is null) break;
                        Cache.Remove(old.Key);
                        CachedAt.Remove(old.Key);
                    }
                }
            }, TaskScheduler.Default);
            return request;
        }
    }
    private static async Task<Image?> RequestAsync(string request)
    {
        using var timeout = new CancellationTokenSource(requestTimeout);
        try { await WorkerGate.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "RoleReaders", "tbcl-snr-cosmetics.exe");
            if (!File.Exists(helper))
            {
                for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                {
                    var development = Path.Combine(directory.FullName, "artifacts", "snr-cosmetics", "tbcl-snr-cosmetics.exe");
                    if (File.Exists(development)) { helper = development; break; }
                }
            }
            if (!File.Exists(helper)) return null;
            if (worker is null || worker.HasExited)
            {
                worker?.Dispose();
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
                if (testDelayMilliseconds > 0) { start.ArgumentList.Add("--self-test-delay-ms"); start.ArgumentList.Add(testDelayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                worker = Process.Start(start);
            }
            if (worker is null) return null;
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var line = await worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref lastUse, Environment.TickCount64);
            if (line is null || line.Length > 12 * 1024 * 1024) throw new InvalidDataException("SNR image worker response exceeded its limit");
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("image", out var image)) return null;
            var png = image.GetBytesFromBase64();
            if (png.Length > 8 * 1024 * 1024) throw new InvalidDataException("SNR image exceeded its limit");
            var adaptive = json.RootElement.GetProperty("adaptive");
            var layout = json.RootElement.GetProperty("isSNR");
            return new Image(png, adaptive.ValueKind == JsonValueKind.Null ? null : adaptive.GetBoolean(),
                layout.ValueKind == JsonValueKind.Null ? null : layout.GetBoolean());
        }
        catch
        {
            StopWorker();
            throw;
        }
        finally { WorkerGate.Release(); }
    }
    private static void StopWorker()
    {
        try { if (worker is { HasExited: false }) worker.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        worker?.Dispose(); worker = null;
    }
    private static void CloseIdle()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref lastUse) < 60000 || !WorkerGate.Wait(0)) return;
        try { StopWorker(); } finally { WorkerGate.Release(); }
    }
    internal static System.Windows.Media.Imaging.BitmapSource Tint(System.Windows.Media.Imaging.BitmapSource image,
        bool adaptive, System.Windows.Media.Color main, System.Windows.Media.Color shadow) =>
        adaptive ? AvatarImageFactory.Recolor(image, main, shadow) : image;
    internal static void VerifyTint()
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(4, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
            null, new byte[] { 0,0,255,255, 255,0,0,128, 255,255,255,64, 12,34,56,0 }, 16);
        source.Freeze();
        var first = Tint(source, true, System.Windows.Media.Colors.Red, System.Windows.Media.Colors.Blue);
        var second = Tint(source, true, System.Windows.Media.Colors.Green, System.Windows.Media.Colors.Yellow);
        var a = new byte[16]; var b = new byte[16]; first.CopyPixels(a, 16, 0); second.CopyPixels(b, 16, 0);
        if (a.AsSpan().SequenceEqual(b) || new[] { 3,7,11,15 }.Any(i => a[i] != b[i]) ||
            a[3] != 255 || a[7] != 128 || a[11] != 64 || a[15] != 0 ||
            !ReferenceEquals(Tint(source, false, System.Windows.Media.Colors.Green, System.Windows.Media.Colors.Yellow), source))
            throw new InvalidOperationException("SNR adaptive/fixed metadata or alpha preservation failed");
        Console.WriteLine("[PASS] SNR adaptive two-color pixels, unchanged alpha and fixed-image precedence");
    }
    internal static async Task VerifySlowWorkerAsync(string executable)
    {
        await WorkerGate.WaitAsync();
        try { StopWorker(); requestTimeout = TimeSpan.FromMilliseconds(300); testDelayMilliseconds = 2000; }
        finally { WorkerGate.Release(); }
        try
        {
            var watch = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 40).Select(i => GetAsync(executable, "Modded_Queue_Test" + i, CosmeticPart.Hat)).ToArray();
            lock (CacheGate)
                if (Cache.Count > 64 || Cache.Values.Count(value => !value.IsCompleted) > 8)
                    throw new InvalidOperationException("SNR worker accepted an unbounded request queue");
            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("SNR worker queue ignored request deadlines");
        }
        finally
        {
            await WorkerGate.WaitAsync();
            try { StopWorker(); requestTimeout = TimeSpan.FromSeconds(15); testDelayMilliseconds = 0; }
            finally { WorkerGate.Release(); }
        }
        Console.WriteLine("[PASS] Real slow SNR worker: forty unique requests bounded to eight pending, total deadline and stalled-worker cleanup");
    }
}
