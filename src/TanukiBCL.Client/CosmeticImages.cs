using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace TanukiBCL.Client;

internal static class CosmeticImages
{
    private static readonly object Gate = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private static Task<CosmeticCatalog>? catalog;
    private static Task<SnrCosmeticCatalog>? snrCatalog;
    private static readonly Dictionary<Uri, Task<BitmapSource>> Images = [];

    public static async Task<CosmeticCatalog> GetCatalogAsync()
    {
        Task<CosmeticCatalog> request;
        lock (Gate) request = catalog ??= CosmeticCatalog.DownloadAsync(CancellationToken.None);
        try { return await request.ConfigureAwait(false); }
        catch { lock (Gate) { if (catalog == request) catalog = null; } throw; }
    }

    public static async Task<BitmapSource> GetImageAsync(Uri uri)
    {
        Task<BitmapSource> request;
        lock (Gate)
        {
            if (!Images.TryGetValue(uri, out request!))
            {
                if (Images.Count >= 256) Images.Clear();
                request = LoadAsync(uri);
                Images[uri] = request;
            }
        }
        try { return await request.ConfigureAwait(false); }
        catch { lock (Gate) { if (Images.TryGetValue(uri, out var current) && current == request) Images.Remove(uri); } throw; }
    }

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
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        if (image.PixelWidth > 4096 || image.PixelHeight > 4096) throw new InvalidDataException("Cosmetic image too large");
        image.Freeze();
        return image;
    }
}
