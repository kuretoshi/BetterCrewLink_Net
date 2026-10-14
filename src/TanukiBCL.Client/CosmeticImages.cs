using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace TanukiBCL.Client;

internal static class CosmeticImages
{
    private const int MaxDecodeDimension = 512;
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<BitmapSource, ImageDimensions> OriginalSizes = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private static Task<CosmeticCatalog>? catalog;
    private static Task<SnrCosmeticCatalog>? snrCatalog;
    // One lobby shows at most 15 players x a few layers; the LRU keeps them while a long
    // session through many public lobbies no longer grows toward 64 MiB of decoded images.
    private static readonly CosmeticImageCache Images = new(128, 32L * 1024 * 1024, LoadAsync);

    internal static void ReleaseCachedImages() => Images.Clear();

    public static async Task<CosmeticCatalog> GetCatalogAsync()
    {
        Task<CosmeticCatalog> request;
        lock (Gate) request = catalog ??= CosmeticCatalog.DownloadAsync(CancellationToken.None);
        try { return await request.ConfigureAwait(false); }
        catch { lock (Gate) { if (catalog == request) catalog = null; } throw; }
    }

    public static Task<BitmapSource> GetImageAsync(Uri uri) => Images.GetAsync(uri);

    internal static (int Width, int Height) OriginalSize(BitmapSource image) =>
        OriginalSizes.TryGetValue(image, out var size)
            ? (size.Width, size.Height) : (image.PixelWidth, image.PixelHeight);

    internal static async Task<SnrCosmeticCatalog> GetSnrCatalogAsync()
    {
        Task<SnrCosmeticCatalog> request;
        lock (Gate) request = snrCatalog ??= SnrCosmeticCatalog.DownloadAsync();
        try { return await request.ConfigureAwait(false); }
        catch { lock (Gate) { if (snrCatalog == request) snrCatalog = null; } throw; }
    }

    private static async Task<BitmapSource> LoadAsync(Uri uri)
    {
        byte[] bytes;
        if (uri.IsFile)
        {
            if (new FileInfo(uri.LocalPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Cosmetic image too large");
            bytes = await File.ReadAllBytesAsync(uri.LocalPath).ConfigureAwait(false);
        }
        else bytes = await Http.GetByteArrayAsync(uri).ConfigureAwait(false);
        return Decode(bytes);
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var (width, height) = ReadDimensions(bytes, stream);
        if (width > 4096 || height > 4096) throw new InvalidDataException("Cosmetic image too large");
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (Math.Max(width, height) > MaxDecodeDimension)
        {
            if (width >= height) image.DecodePixelWidth = MaxDecodeDimension;
            else image.DecodePixelHeight = MaxDecodeDimension;
        }
        image.EndInit();
        image.Freeze();
        OriginalSizes.Add(image, new ImageDimensions(width, height));
        return image;
    }

    private static (int Width, int Height) ReadDimensions(byte[] bytes, Stream stream)
    {
        // The cosmetic packs sampled here use PNG. Its fixed IHDR header avoids creating a
        // second WPF decoder just to learn the dimensions before the actual decode.
        if (bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 &&
            bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 &&
            bytes[7] == 10 && bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 &&
            bytes[11] == 13 && bytes[12] == 'I' && bytes[13] == 'H' &&
            bytes[14] == 'D' && bytes[15] == 'R')
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > 4096 || height > 4096)
                throw new InvalidDataException("Cosmetic image too large");
            return ((int)width, (int)height);
        }
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private sealed record ImageDimensions(int Width, int Height);

    internal static void VerifyCache()
    {
        var pixels = new byte[16];
        var image = BitmapSource.Create(2, 2, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();
        var loads = new Dictionary<Uri, int>();
        var cache = new CosmeticImageCache(3, 32, uri =>
        {
            loads[uri] = loads.GetValueOrDefault(uri) + 1;
            return Task.FromResult(image);
        });
        var first = new Uri("https://example.invalid/first.png");
        var second = new Uri("https://example.invalid/second.png");
        var third = new Uri("https://example.invalid/third.png");
        cache.GetAsync(first).GetAwaiter().GetResult();
        cache.GetAsync(second).GetAwaiter().GetResult();
        cache.GetAsync(first).GetAwaiter().GetResult();
        cache.GetAsync(third).GetAwaiter().GetResult();
        if (cache.CachedBytes != 32 || cache.Count != 2 || loads[first] != 1)
            throw new InvalidOperationException("Cosmetic cache budget or LRU retention failed.");
        cache.GetAsync(second).GetAwaiter().GetResult();
        if (loads[second] != 2 || cache.CachedBytes > 32)
            throw new InvalidOperationException("Evicted cosmetic was not reloaded within budget.");

        var loading = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentLoads = 0;
        var concurrent = new CosmeticImageCache(3, 32, _ => { concurrentLoads++; return loading.Task; });
        var pendingA = concurrent.GetAsync(first);
        var pendingB = concurrent.GetAsync(first);
        if (concurrentLoads != 1) throw new InvalidOperationException("Concurrent cosmetic requests were duplicated.");
        loading.SetResult(image);
        if (!ReferenceEquals(pendingA.GetAwaiter().GetResult(), pendingB.GetAwaiter().GetResult()))
            throw new InvalidOperationException("Concurrent cosmetic requests returned different images.");

        var countLimited = new CosmeticImageCache(2, 1024, _ => Task.FromResult(image));
        countLimited.GetAsync(first).GetAwaiter().GetResult();
        countLimited.GetAsync(second).GetAwaiter().GetResult();
        countLimited.GetAsync(third).GetAwaiter().GetResult();
        if (countLimited.Count != 2) throw new InvalidOperationException("Cosmetic cache entry limit failed.");

        var oversized = new CosmeticImageCache(3, 8, _ => Task.FromResult(image));
        oversized.GetAsync(first).GetAwaiter().GetResult();
        if (oversized.Count != 0 || oversized.CachedBytes != 0)
            throw new InvalidOperationException("Oversized cosmetic remained cached.");

        var attempts = 0;
        var retry = new CosmeticImageCache(3, 32, _ =>
            ++attempts == 1 ? Task.FromException<BitmapSource>(new IOException("test")) : Task.FromResult(image));
        try { retry.GetAsync(first).GetAwaiter().GetResult(); }
        catch (IOException) { }
        retry.GetAsync(first).GetAwaiter().GetResult();
        if (attempts != 2 || retry.Count != 1)
            throw new InvalidOperationException("Failed cosmetic download was retained in cache.");
        var activeImage = cache.GetAsync(first).GetAwaiter().GetResult();
        var loadsBeforeClear = loads[first];
        cache.Clear();
        if (cache.Count != 0 || cache.CachedBytes != 0 || !activeImage.IsFrozen)
            throw new InvalidOperationException("Clearing the cosmetic cache invalidated a displayed image.");
        cache.GetAsync(first).GetAwaiter().GetResult();
        if (loads[first] != loadsBeforeClear + 1)
            throw new InvalidOperationException("A cleared cosmetic was not reloaded on demand.");
        var pendingLoad = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingCache = new CosmeticImageCache(3, 32, _ => pendingLoad.Task);
        var pending = pendingCache.GetAsync(first);
        pendingCache.Clear();
        pendingLoad.SetResult(image);
        if (!ReferenceEquals(pending.GetAwaiter().GetResult(), image) ||
            pendingCache.Count != 0 || pendingCache.CachedBytes != 0)
            throw new InvalidOperationException("A completed stale request repopulated the cleared cache.");
        var largePixels = new byte[1024 * 768 * 4];
        var largeSource = BitmapSource.Create(1024, 768, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, largePixels, 1024 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(largeSource));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        var reduced = Decode(encoded.ToArray());
        var visor = new CosmeticAsset(new Uri("https://example.invalid/visor.png"),
            false, "-52%", "-18px", "140%", true);
        var sourceSize = OriginalSize(reduced);
        if (reduced.PixelWidth != 512 || reduced.PixelHeight != 384 ||
            sourceSize != (1024, 768) || !reduced.IsFrozen || OriginalSize(image) != (2, 2) ||
            SnrCosmeticCatalog.WithImageSize(visor, sourceSize.Width, sourceSize.Height) !=
            SnrCosmeticCatalog.WithImageSize(visor, 1024, 768))
            throw new InvalidOperationException("Cosmetic decode bound or original dimensions failed.");
        var tallSource = BitmapSource.Create(768, 1024, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, largePixels, 768 * 4);
        encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(tallSource));
        encoded.SetLength(0);
        encoded.Position = 0;
        encoder.Save(encoded);
        var tall = Decode(encoded.ToArray());
        if (tall.PixelWidth != 384 || tall.PixelHeight != 512 || OriginalSize(tall) != (768, 1024))
            throw new InvalidOperationException("Portrait cosmetic decode bound failed.");
        var oversizedPng = encoded.ToArray();
        oversizedPng[18] = 16;
        oversizedPng[19] = 1;
        try { Decode(oversizedPng); throw new InvalidOperationException("Oversized PNG was accepted."); }
        catch (InvalidDataException) { }
        var jpeg = new JpegBitmapEncoder();
        jpeg.Frames.Add(BitmapFrame.Create(largeSource));
        encoded.SetLength(0);
        encoded.Position = 0;
        jpeg.Save(encoded);
        var jpegImage = Decode(encoded.ToArray());
        if (jpegImage.PixelWidth != 512 || jpegImage.PixelHeight != 384 ||
            OriginalSize(jpegImage) != (1024, 768))
            throw new InvalidOperationException("Non-PNG cosmetic fallback lost dimensions.");
        Console.WriteLine("[PASS] Cosmetic image cache byte budget, LRU eviction and retry");
        Console.WriteLine("[PASS] Cosmetic cache clears without invalidating displayed or pending images");
        Console.WriteLine("[PASS] Large cosmetic decodes smaller without losing original layout dimensions");
        Console.WriteLine("[PASS] PNG header dimensions and non-PNG fallback");
    }
}

internal sealed class CosmeticImageCache(int maxEntries, long maxBytes, Func<Uri, Task<BitmapSource>> load)
{
    private readonly object gate = new();
    private readonly Dictionary<Uri, Entry> images = [];
    private readonly LinkedList<Uri> order = [];
    private long cachedBytes;

    internal int Count { get { lock (gate) return images.Count; } }
    internal long CachedBytes { get { lock (gate) return cachedBytes; } }

    internal void Clear()
    {
        lock (gate)
        {
            images.Clear();
            order.Clear();
            cachedBytes = 0;
        }
    }

    internal async Task<BitmapSource> GetAsync(Uri uri)
    {
        Entry entry;
        lock (gate)
        {
            if (images.TryGetValue(uri, out var existing))
            {
                entry = existing;
                order.Remove(entry.Node);
                order.AddLast(entry.Node);
            }
            else
            {
                var node = order.AddLast(uri);
                Task<BitmapSource> request;
                try { request = load(uri); }
                catch { order.Remove(node); throw; }
                entry = new Entry(request, node);
                images.Add(uri, entry);
                Trim();
            }
        }

        try
        {
            var image = await entry.Request.ConfigureAwait(false);
            lock (gate)
            {
                if (images.TryGetValue(uri, out var current) && ReferenceEquals(current, entry) && entry.Bytes == 0)
                {
                    // Decoded WPF pixels dominate the encoded image size. Use at
                    // least four bytes/pixel when budgeting the retained cache.
                    entry.Bytes = checked((long)image.PixelWidth * image.PixelHeight *
                        Math.Max(4, (image.Format.BitsPerPixel + 7) / 8));
                    cachedBytes += entry.Bytes;
                    Trim();
                }
            }
            return image;
        }
        catch
        {
            lock (gate)
            {
                if (images.TryGetValue(uri, out var current) && ReferenceEquals(current, entry)) Remove(uri, entry);
            }
            throw;
        }
    }

    private void Trim()
    {
        while (images.Count > maxEntries || cachedBytes > maxBytes)
        {
            var oldest = order.First!;
            Remove(oldest.Value, images[oldest.Value]);
        }
    }

    private void Remove(Uri uri, Entry entry)
    {
        images.Remove(uri);
        order.Remove(entry.Node);
        cachedBytes -= entry.Bytes;
    }

    private sealed class Entry(Task<BitmapSource> request, LinkedListNode<Uri> node)
    {
        internal Task<BitmapSource> Request { get; } = request;
        internal LinkedListNode<Uri> Node { get; } = node;
        internal long Bytes { get; set; }
    }
}
