using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

internal enum NosCosmeticPart { Skin, Hat, HatBack, Visor, BodyMask }

/// <summary>
/// Revision is set when the file was parsed. The JSON text itself is not retained; the debug
/// window reads Path again only while it is open.
/// </summary>
internal sealed record NosLoadedContentsStatus(string Path, string Status, string? Revision = null);

/// <summary>
/// Port of TanukiBCL 3.2.9 main/nosContents.ts and nosAddonImages.ts: reads the game's
/// BepInEx/MoreCosmic/LoadedContents.json and renders only PNGs registered under the game folder.
/// </summary>
internal sealed class NosCosmeticContents
{
    internal const int CanvasWidth = 300;
    internal const int CanvasHeight = 375;
    internal const int ManifestVersion = 20261005;
    private const long MaxImageBytes = 16 * 1024 * 1024;

    public static NosCosmeticContents Shared { get; } = new();

    private sealed record ZipImage(string Archive, string Entry);

    private sealed record Asset(object File, object? Extra, bool ExtraInFront, int Columns, int Rows,
        bool Adaptive, bool Mask = false);

    private readonly object gate = new();
    private readonly Dictionary<string, Asset> assets = [];
    private readonly Dictionary<string, Dictionary<NosCosmeticPart, string>> costumes = [];
    // Each rendered layer is a 300x375 BGRA canvas (~440 KiB). Avatars keep the images they show,
    // so this LRU only needs the recent ones that the main window and overlay share.
    internal const long MaxCachedImageBytes = 24L * 1024 * 1024;
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> images = [];
    private readonly LinkedList<(string Key, BitmapSource Image)> imageOrder = new();
    private Dictionary<string, Dictionary<string, ZipImage>> addonImages = [];
    private DateTimeOffset nextRead;
    private string signature = string.Empty;
    private string root = string.Empty;
    private NosLoadedContentsStatus? result;

    public NosLoadedContentsStatus Update(string gameDirectory)
    {
        var file = Path.Combine(gameDirectory, "BepInEx", "MoreCosmic", "LoadedContents.json");
        lock (gate)
        {
            if (result?.Path == file && DateTimeOffset.UtcNow < nextRead) return result;
            nextRead = DateTimeOffset.UtcNow.AddSeconds(2);
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) throw new FileNotFoundException("ファイルがありません");
                if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException("ファイルが4MBを超えています");
                var current = $"{file}:{info.LastWriteTimeUtc.Ticks}:{info.Length}";
                if (current == signature && result is not null) return result;
                Clear();
                root = RealPath(gameDirectory);
                addonImages = IndexAddonImages(root);
                // Stream one costume at a time: the whole file as a string or JsonDocument would
                // land on the large object heap and its pooled buffers stay committed afterwards.
                Manifest? manifest;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024))
                    manifest = JsonSerializer.Deserialize<Manifest>(stream);
                var supported = manifest is not null && manifest.Version.ValueKind == JsonValueKind.Number &&
                    manifest.Version.TryGetInt32(out var number) && number == ManifestVersion;
                if (supported)
                {
                    Register(manifest!.Hats, "hat", current);
                    Register(manifest.Visors, "visor", current);
                    Register(manifest.Skins, "skin", current);
                }
                signature = current;
                result = new NosLoadedContentsStatus(file,
                    supported ? "読み取り成功" : "画像表示は未対応の定義バージョンです", current);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                Clear();
                result = new NosLoadedContentsStatus(file, $"読み取り失敗: {error.Message}");
            }
            return result;
        }
    }

    public IReadOnlyDictionary<NosCosmeticPart, string>? Cosmetics(NosPlayerData? player)
    {
        if (player is null) return null;
        var parts = new Dictionary<NosCosmeticPart, string>();
        lock (gate)
        {
            foreach (var (kind, costume) in new[] { ("skin", player.Skin), ("hat", player.Hat), ("visor", player.Visor) })
                if (costumes.TryGetValue($"{kind}:{costume?.Name ?? ""}", out var found))
                    foreach (var (part, key) in found) parts[part] = key;
        }
        return parts.Count > 0 ? parts : null;
    }

    // v3.2.13: lobby outfits are available before NoS publishes round PlayerData.
    internal static NosPlayerData? LobbyPlayer(Player player, Color fallbackColor)
    {
        if (player.Disconnected) return null;
        var color = player.NosLobbyColor is { Length: 7 } hex && hex[0] == '#' &&
            uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallbackColor;
        return new NosPlayerData
        {
            Skin = new NosCostumeData(player.AppearanceSkinId),
            Hat = new NosCostumeData(player.AppearanceHatId),
            Visor = new NosCostumeData(player.AppearanceVisorId),
            ColorR = color.R / 255d,
            ColorG = color.G / 255d,
            ColorB = color.B / 255d
        };
    }

    public BitmapSource? Image(string key, double red, double green, double blue)
    {
        Asset? asset;
        lock (gate)
        {
            if (!assets.TryGetValue(key, out asset)) return null;
            if (images.TryGetValue($"{key}:{red},{green},{blue}", out var cached))
            {
                imageOrder.Remove(cached);
                imageOrder.AddFirst(cached);
                return cached.Value.Image;
            }
        }
        BitmapSource? image;
        try { image = Render(asset, [red, green, blue]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or NotSupportedException or ArgumentException or FileFormatException or
            InvalidOperationException or OverflowException)
        {
            return null;
        }
        lock (gate)
        {
            // Another manifest revision may have replaced the asset while rendering.
            if (!assets.TryGetValue(key, out var current) || !ReferenceEquals(current, asset)) return null;
            var cacheKey = $"{key}:{red},{green},{blue}";
            if (images.Remove(cacheKey, out var stale)) imageOrder.Remove(stale);
            images[cacheKey] = imageOrder.AddFirst((cacheKey, image));
            while (images.Count * ImageBytes > MaxCachedImageBytes && imageOrder.Last is { } oldest)
            {
                imageOrder.RemoveLast();
                images.Remove(oldest.Value.Key);
            }
        }
        return image;
    }

    private const long ImageBytes = CanvasWidth * CanvasHeight * 4;

    internal int CachedImageCount { get { lock (gate) return images.Count; } }

    private void Clear()
    {
        signature = string.Empty;
        assets.Clear();
        costumes.Clear();
        images.Clear();
        imageOrder.Clear();
    }

    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        return info.LinkTarget is null ? full : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
    }

    private bool Inside(string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.Length > 0 && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private string? LocalFile(string relatedPath, string address)
    {
        try
        {
            static string Native(string value) => value.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var file = RealPath(Path.Combine(root, Native(relatedPath), Native(address)));
            if (!Inside(file) || !string.Equals(Path.GetExtension(file), ".png", StringComparison.OrdinalIgnoreCase))
                return null;
            var info = new FileInfo(file);
            return info.Exists && info.Length <= MaxImageBytes ? file : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    private sealed class Manifest
    {
        public JsonElement Version { get; set; }
        public Dictionary<string, JsonElement>? Hats { get; set; }
        public Dictionary<string, JsonElement>? Visors { get; set; }
        public Dictionary<string, JsonElement>? Skins { get; set; }
    }

    private void Register(Dictionary<string, JsonElement>? collection, string kind, string revision)
    {
        if (collection is null) return;
        foreach (var (id, costume) in collection)
        {
            if (costume.ValueKind != JsonValueKind.Object || !costume.TryGetProperty("Images", out var imageList) ||
                imageList.ValueKind != JsonValueKind.Array) continue;
            string? related = costume.TryGetProperty("RelatedRawLocalPath", out var relatedValue) &&
                relatedValue.ValueKind == JsonValueKind.String ? relatedValue.GetString() : null;
            var adaptive = costume.TryGetProperty("Adaptive", out var adaptiveValue) &&
                adaptiveValue.ValueKind == JsonValueKind.True;
            object? FindImage(string address) => related is not null
                ? LocalFile(related, address)
                : addonImages.TryGetValue(id, out var files) &&
                  files.TryGetValue(address.Replace('\\', '/'), out var zipped) ? zipped : null;
            static string? Text(JsonElement image, string name) =>
                image.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var parts = new Dictionary<NosCosmeticPart, string>();
            (NosCosmeticPart Part, string Layer)[] layers = kind == "hat"
                ? [(NosCosmeticPart.Hat, "Main"), (NosCosmeticPart.HatBack, "Back")]
                : [(kind == "visor" ? NosCosmeticPart.Visor : NosCosmeticPart.Skin, "Main")];
            foreach (var (part, layer) in layers)
            {
                var image = imageList.EnumerateArray().FirstOrDefault(candidate =>
                    candidate.ValueKind == JsonValueKind.Object && Text(candidate, "Layer") == layer);
                if (image.ValueKind != JsonValueKind.Object || Text(image, "Address") is not { } address ||
                    !image.TryGetProperty("DivisionX", out var divisionX) || !divisionX.TryGetInt32(out var columns) ||
                    !image.TryGetProperty("DivisionY", out var divisionY) || !divisionY.TryGetInt32(out var rows) ||
                    columns is < 1 or > 256 || rows is < 1 or > 256)
                    continue;
                if (FindImage(address) is not { } file) continue;
                var extra = Text(image, "ExAddress") is { } extraAddress ? FindImage(extraAddress) : null;
                var key = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{revision}:{kind}:{id}:{PartName(part)}"))).ToLowerInvariant();
                assets[key] = new Asset(file, extra,
                    image.TryGetProperty("ExIsFront", out var front) && front.ValueKind == JsonValueKind.True,
                    columns, rows, adaptive);
                parts[part] = key;
                if (kind == "hat" && layer == "Main" && Text(image, "MaskAddress") is { } maskAddress &&
                    FindImage(maskAddress) is { } mask)
                {
                    assets[key + "mask"] = new Asset(mask, null, false, columns, rows, false, Mask: true);
                    parts[NosCosmeticPart.BodyMask] = key + "mask";
                }
            }
            if (parts.Count == 0) continue;
            costumes[$"{kind}:{id}"] = parts;
            if (costume.TryGetProperty("ProductId", out var product) && product.ValueKind == JsonValueKind.String)
                costumes[$"{kind}:{product.GetString()}"] = parts;
        }
    }

    private static string PartName(NosCosmeticPart part) => part switch
    {
        NosCosmeticPart.HatBack => "hatBack",
        NosCosmeticPart.BodyMask => "bodyMask",
        _ => part.ToString().ToLowerInvariant()
    };

    /// <summary>Index addon ZIP entries without extracting or modifying the game folder.</summary>
    private static Dictionary<string, Dictionary<string, ZipImage>> IndexAddonImages(string gameDirectory)
    {
        var result = new Dictionary<string, Dictionary<string, ZipImage>>();
        var directory = Path.Combine(gameDirectory, "Addons");
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var archivePath = RealPath(path);
                if (Path.GetRelativePath(gameDirectory, archivePath).StartsWith("..", StringComparison.Ordinal)) continue;
                if (new FileInfo(archivePath).Length > 512L * 1024 * 1024) continue;
                using var archive = ZipFile.OpenRead(archivePath);
                var all = archive.Entries
                    .Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.Split('/').Contains(".."))
                    .GroupBy(entry => entry.FullName).ToDictionary(group => group.Key, group => group.Last());
                foreach (var (name, entry) in all)
                {
                    if (!name.EndsWith("MoreCosmic/Contents.json", StringComparison.Ordinal) ||
                        entry.Length > 4 * 1024 * 1024) continue;
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    using var contents = JsonDocument.Parse(reader.ReadToEnd().TrimStart('﻿'));
                    var prefix = name[..^"Contents.json".Length];
                    foreach (var (category, idPrefix) in new[] { ("hats", "noshat_"), ("visors", "nosvisor_") })
                    {
                        if (!contents.RootElement.TryGetProperty(category, out var list) ||
                            list.ValueKind != JsonValueKind.Array) continue;
                        var folder = $"{prefix}{category}/";
                        foreach (var costume in list.EnumerateArray())
                        {
                            if (costume.ValueKind != JsonValueKind.Object ||
                                !costume.TryGetProperty("Author", out var author) || author.ValueKind != JsonValueKind.String ||
                                !costume.TryGetProperty("Name", out var costumeName) || costumeName.ValueKind != JsonValueKind.String)
                                continue;
                            result[$"{idPrefix}{author.GetString()}_{costumeName.GetString()}"] = all.Keys
                                .Where(file => file.StartsWith(folder, StringComparison.Ordinal) &&
                                    file.EndsWith(".png", StringComparison.Ordinal))
                                .ToDictionary(file => file[folder.Length..], file => new ZipImage(archivePath, file));
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or NotSupportedException or ArgumentException)
            {
                // Ignore an unavailable or unsupported addon; folder cosmetics still work.
            }
        }
        return result;
    }

    private byte[] ReadImage(object file)
    {
        if (file is string path)
        {
            // Recheck containment before each read, including files replaced by symbolic links.
            if (!Inside(RealPath(path))) throw new InvalidDataException("Image unavailable");
            return File.ReadAllBytes(path);
        }
        var image = (ZipImage)file;
        if (!Inside(RealPath(image.Archive))) throw new InvalidDataException("Image unavailable");
        using var archive = ZipFile.OpenRead(image.Archive);
        var entry = archive.GetEntry(image.Entry) ?? throw new InvalidDataException("Invalid ZIP image");
        if (entry.Length > MaxImageBytes || entry.CompressedLength > MaxImageBytes)
            throw new InvalidDataException("Invalid ZIP image");
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        if (buffer.Length != entry.Length) throw new InvalidDataException("Invalid ZIP image size");
        return buffer.ToArray();
    }

    private BitmapSource Render(Asset asset, double[] rgb)
    {
        if (!rgb.All(value => double.IsFinite(value) && value is >= 0 and <= 1))
            throw new ArgumentException("Invalid color");
        var output = new byte[CanvasWidth * CanvasHeight * 4];
        if (asset.Mask) Array.Fill(output, (byte)255);
        static int Round(double value) => (int)Math.Floor(value + 0.5);
        double[] tint = [0.604, 0.792, 0.835];
        void Draw(object file, bool adaptive)
        {
            var bytes = ReadImage(file);
            if (bytes.Length < 24 ||
                (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) *
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) > MaxImageBytes)
                throw new InvalidDataException("Image too large");
            // Only the top-left sprite frame is drawn. Without a load cache WIC decodes just the
            // rows of that frame instead of keeping the whole sheet (often several MiB) decoded.
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = new PngBitmapDecoder(stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width % asset.Columns != 0 || height % asset.Rows != 0)
                throw new InvalidDataException("Invalid sprite divisions");
            int w = width / asset.Columns, h = height / asset.Rows;
            var data = new byte[w * h * 4];
            frame.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), data, w * 4, 0);
            int left = Round((CanvasWidth - w) * 0.53), top = Round((CanvasHeight - h) * 0.425);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    int dx = left + x, dy = top + y;
                    if (dx < 0 || dy < 0 || dx >= CanvasWidth || dy >= CanvasHeight) continue;
                    int source = (y * w + x) * 4, target = (dy * CanvasWidth + dx) * 4;
                    // data is BGRA; output is RGBA like the upstream Jimp bitmap.
                    double r = data[source + 2], g = data[source + 1], b = data[source];
                    double a = data[source + 3] / 255d, oldA = output[target + 3] / 255d;
                    var alpha = a + oldA * (1 - a);
                    if (alpha == 0) continue;
                    if (asset.Mask)
                    {
                        output[target + 3] = (byte)(255 - Round(r * a));
                        continue;
                    }
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var pixel = adaptive
                            ? Math.Min(255, r * rgb[channel] + g * tint[channel] + b * rgb[channel] * 0.55)
                            : channel switch { 0 => r, 1 => g, _ => b };
                        output[target + channel] = (byte)Round((pixel * a + output[target + channel] * oldA * (1 - a)) / alpha);
                    }
                    output[target + 3] = (byte)Round(alpha * 255);
                }
        }
        if (asset.Extra is not null && !asset.ExtraInFront) Draw(asset.Extra, false);
        Draw(asset.File, asset.Adaptive);
        if (asset.Extra is not null && asset.ExtraInFront) Draw(asset.Extra, false);
        for (var i = 0; i < output.Length; i += 4) (output[i], output[i + 2]) = (output[i + 2], output[i]);
        var bitmap = BitmapSource.Create(CanvasWidth, CanvasHeight, 96, 96, PixelFormats.Bgra32, null, output,
            CanvasWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Developer check against a real NoS install: resolve published names and save the rendered layers.</summary>
    internal static void VerifyLive(string gameDirectory, string skin, string hat, string visor, string output)
    {
        var contents = new NosCosmeticContents();
        var status = contents.Update(gameDirectory);
        Console.WriteLine($"LoadedContents: {status.Path} / {status.Status} / addons={contents.addonImages.Count}");
        var player = new NosPlayerData
        {
            ColorR = 0.78, ColorG = 0.07, ColorB = 0.07,
            Skin = new NosCostumeData(skin), Hat = new NosCostumeData(hat), Visor = new NosCostumeData(visor)
        };
        var parts = contents.Cosmetics(player) ?? throw new InvalidOperationException("No NoS cosmetics resolved");
        foreach (var (part, key) in parts)
        {
            var image = contents.Image(key, player.ColorR, player.ColorG, player.ColorB) ??
                throw new InvalidOperationException($"NoS {part} image could not be rendered");
            var path = Path.Combine(output, $"nos-{part}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine($"  {part}: {path}");
        }
        Console.WriteLine($"[PASS] Live NoS cosmetics resolved {parts.Count} layers");
    }

    internal static void Verify()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var (r, g, b, a) = pixel(x, y);
                    var i = (y * width + x) * 4;
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
                }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                null, pixels, width * 4)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        static (byte B, byte G, byte R, byte A) Pixel(BitmapSource image, int x, int y)
        {
            var pixel = new byte[4];
            image.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[0], pixel[1], pixel[2], pixel[3]);
        }
        var game = Path.Combine(Path.GetTempPath(), $"tanuki-nos-contents-{Guid.NewGuid():N}");
        try
        {
            var local = Directory.CreateDirectory(Path.Combine(game, "BepInEx", "MoreCosmic", "Local", "MyHat"));
            Directory.CreateDirectory(Path.Combine(game, "Addons"));
            // Two-column sprite sheet: only the top-left frame (red) may be drawn.
            File.WriteAllBytes(Path.Combine(local.FullName, "main.png"),
                Png(4, 2, (x, _) => x < 2 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255)));
            File.WriteAllBytes(Path.Combine(local.FullName, "mask.png"),
                Png(2, 2, (_, _) => ((byte)255, (byte)255, (byte)255, (byte)255)));
            File.WriteAllBytes(Path.Combine(local.FullName, "adaptive.png"),
                Png(2, 2, (_, _) => ((byte)200, (byte)0, (byte)0, (byte)255)));
            File.WriteAllBytes(Path.Combine(game, "outside.png"), Png(1, 1, (_, _) => ((byte)1, (byte)2, (byte)3, (byte)255)));
            using (var archive = ZipFile.Open(Path.Combine(game, "Addons", "pack.zip"), ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("Pack/MoreCosmic/Contents.json").Open()))
                    writer.Write("""{"visors":[{"Author":"Me","Name":"Shade"}]}""");
                using var image = archive.CreateEntry("Pack/MoreCosmic/visors/Shade.png").Open();
                image.Write(Png(1, 1, (_, _) => ((byte)0, (byte)255, (byte)0, (byte)255)));
            }
            var manifest = """
                {"Version":20261005,
                 "Hats":{"MyHat":{"ProductId":"nos_hat","RelatedRawLocalPath":"BepInEx/MoreCosmic/Local/MyHat",
                   "Images":[{"Layer":"Main","Address":"main.png","MaskAddress":"mask.png","DivisionX":2,"DivisionY":1}]},
                  "Escape":{"RelatedRawLocalPath":"BepInEx/MoreCosmic/Local/MyHat",
                   "Images":[{"Layer":"Main","Address":"../../../../../outside.png","DivisionX":1,"DivisionY":1}]}},
                 "Visors":{"nosvisor_Me_Shade":{"Images":[{"Layer":"Main","Address":"Shade.png","DivisionX":1,"DivisionY":1}]}},
                 "Skins":{"Suit":{"Adaptive":true,"RelatedRawLocalPath":"BepInEx/MoreCosmic/Local/MyHat",
                   "Images":[{"Layer":"Main","Address":"adaptive.png","DivisionX":1,"DivisionY":1}]}}}
                """;
            // The game writes a UTF-8 BOM; the streaming reader must accept it like the old TrimStart.
            File.WriteAllText(Path.Combine(game, "BepInEx", "MoreCosmic", "LoadedContents.json"), manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var contents = new NosCosmeticContents();
            var status = contents.Update(game);
            Require(status.Status == "読み取り成功" && status.Revision is not null,
                "LoadedContents.json was not read");
            var player = new NosPlayerData
            {
                ColorR = 0.5, ColorG = 0, ColorB = 1,
                Hat = new NosCostumeData("nos_hat"), Visor = new NosCostumeData("nosvisor_Me_Shade"),
                Skin = new NosCostumeData("Suit")
            };
            var parts = contents.Cosmetics(player) ?? throw new InvalidOperationException("No NoS cosmetics resolved");
            Require(parts.ContainsKey(NosCosmeticPart.Hat) && parts.ContainsKey(NosCosmeticPart.BodyMask) &&
                parts.ContainsKey(NosCosmeticPart.Visor) && parts.ContainsKey(NosCosmeticPart.Skin) &&
                !parts.ContainsKey(NosCosmeticPart.HatBack), "NoS ProductId, addon ZIP or skin lookup differs from 3.2.9");
            Require(contents.Cosmetics(new NosPlayerData { Hat = new NosCostumeData("Escape") }) is null,
                "A path outside the game folder was registered");
            var lobby = LobbyPlayer(new Player { AppearanceHatId = "nos_hat", AppearanceVisorId = "nosvisor_Me_Shade",
                AppearanceSkinId = "Suit", NosLobbyColor = "#ff8000" }, Colors.White)!;
            var lobbyParts = contents.Cosmetics(lobby);
            Require(lobbyParts is not null && lobbyParts.ContainsKey(NosCosmeticPart.Hat) &&
                lobbyParts.ContainsKey(NosCosmeticPart.Visor) && lobbyParts.ContainsKey(NosCosmeticPart.Skin) &&
                lobby.ColorR == 1d && lobby.ColorG == 128d / 255d && lobby.ColorB == 0d,
                "NoS lobby outfits or dynamic palette color were lost before PlayerData.");
            Require(LobbyPlayer(new Player { Disconnected = true }, Colors.White) is null &&
                contents.Cosmetics(LobbyPlayer(new Player(), Colors.White)) is null &&
                LobbyPlayer(new Player { NosLobbyColor = "invalid" }, Color.FromRgb(12, 34, 56)) is
                    { ColorR: var red, ColorG: var green, ColorB: var blue } &&
                red == 12d / 255d && green == 34d / 255d && blue == 56d / 255d,
                "NoS lobby disconnect, unequip or fallback color differs from v3.2.13.");
            var hat = contents.Image(parts[NosCosmeticPart.Hat], 0.5, 0, 1)!;
            // A 2x2 frame lands at round((300-2)*.53)=158, round((375-2)*.425)=159.
            Require(hat.PixelWidth == CanvasWidth && hat.PixelHeight == CanvasHeight &&
                Pixel(hat, 158, 159) == (0, 0, 255, 255) && Pixel(hat, 160, 159).A == 0 && Pixel(hat, 157, 159).A == 0,
                "NoS hat frame placement or sprite division differs from 3.2.9");
            var mask = contents.Image(parts[NosCosmeticPart.BodyMask], 0.5, 0, 1)!;
            Require(Pixel(mask, 158, 159).A == 0 && Pixel(mask, 0, 0).A == 255, "NoS body mask differs from 3.2.9");
            var visor = contents.Image(parts[NosCosmeticPart.Visor], 0.5, 0, 1)!;
            Require(Pixel(visor, 158, 159) == (0, 255, 0, 255), "Addon ZIP visor image was not rendered");
            var skin = contents.Image(parts[NosCosmeticPart.Skin], 0.5, 0, 1)!;
            // Adaptive: R*rgb + G*tint + B*rgb*.55 with source (200,0,0) -> (100, 0, 200).
            Require(Pixel(skin, 158, 159) == (200, 0, 100, 255), "Adaptive NoS recolor differs from 3.2.9");
            Require(contents.Image(parts[NosCosmeticPart.Skin], 2, 0, 0) is null, "Invalid NoS color was rendered");
            // Rendered layers stay within the byte budget, keeping the most recently used ones.
            var budget = (int)(MaxCachedImageBytes / ImageBytes);
            var first = contents.Image(parts[NosCosmeticPart.Skin], 0, 0, 0)!;
            BitmapSource? recent = null;
            for (var i = 1; i <= budget + 5; i++)
            {
                recent = contents.Image(parts[NosCosmeticPart.Skin], i / 1000d, 0, 0);
                if (i == budget / 2) Require(ReferenceEquals(contents.Image(parts[NosCosmeticPart.Skin], 0.5, 0, 1), skin),
                    "Recently used NoS image was not reused");
            }
            Require(contents.CachedImageCount == budget &&
                ReferenceEquals(contents.Image(parts[NosCosmeticPart.Skin], (budget + 5) / 1000d, 0, 0), recent) &&
                ReferenceEquals(contents.Image(parts[NosCosmeticPart.Skin], 0.5, 0, 1), skin) &&
                !ReferenceEquals(contents.Image(parts[NosCosmeticPart.Skin], 0, 0, 0), first),
                "NoS image cache exceeded its budget or evicted the wrong image");
            File.WriteAllText(Path.Combine(game, "BepInEx", "MoreCosmic", "LoadedContents.json"), "{\"Version\":1}");
            contents.nextRead = default;
            Require(contents.Update(game).Status == "画像表示は未対応の定義バージョンです" &&
                contents.Cosmetics(player) is null, "Unsupported manifest kept stale cosmetics");
        }
        finally
        {
            try { Directory.Delete(game, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine("[PASS] 3.2.9 NoS LoadedContents, addon ZIP, sprite frame, adaptive color and body mask");
    }
}
