using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TanukiBCL.Client;

internal enum CosmeticPart { Hat, HatBack, Skin, Visor }
internal sealed record CosmeticAsset(Uri Url, bool Adaptive, string Top, string Left, string Width);

// Port of the shared hats.json branch of v3.2.7 renderer/lib/cosmetics.ts.
// SNR remote/local full-player canvases are a separate resolver, not this catalog.
internal sealed class CosmeticCatalog
{
    internal static readonly Uri BaseUri = new("https://cdn.jsdelivr.net/gh/OhMyGuus/BetterCrewLink-Hats@master/");
    private readonly Dictionary<string, Group> groups;
    private CosmeticCatalog(Dictionary<string, Group> groups) => this.groups = groups;
    private sealed record Group(string Top, string Left, string Width, Dictionary<string, Entry> Entries);
    private sealed record Entry(string? Front, string? Back, bool Adaptive, string? Top, string? Left, string? Width);

    public static CosmeticCatalog Parse(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid cosmetics catalog");
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        static string? String(JsonElement element, string key) => element.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        foreach (var group in document.RootElement.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object || !group.Value.TryGetProperty("hats", out var hats) ||
                hats.ValueKind != JsonValueKind.Object) continue;
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var hat in hats.EnumerateObject())
            {
                if (hat.Value.ValueKind != JsonValueKind.Object) continue;
                entries[hat.Name] = new Entry(String(hat.Value, "image"), String(hat.Value, "back_image"),
                    hat.Value.TryGetProperty("multi_color", out var adaptive) && adaptive.ValueKind == JsonValueKind.True,
                    String(hat.Value, "top"), String(hat.Value, "left"), String(hat.Value, "width"));
            }
            groups[group.Name] = new Group(String(group.Value, "defaultTop") ?? "0",
                String(group.Value, "defaultLeft") ?? "0", String(group.Value, "defaultWidth") ?? "0", entries);
        }
        return new CosmeticCatalog(groups);
    }

    public CosmeticAsset? Resolve(string id, string mod, CosmeticPart part)
    {
        // Vanilla definitions take precedence over MOD-specific definitions.
        foreach (var key in new[] { "NONE", mod }.Distinct(StringComparer.Ordinal))
        {
            if (!groups.TryGetValue(key, out var group) || !group.Entries.TryGetValue(id, out var item)) continue;
            var image = part == CosmeticPart.HatBack ? item.Back : item.Front;
            if (string.IsNullOrEmpty(image)) return null;
            // Remote catalog text must never choose an arbitrary host/local file.
            if (key.IndexOfAny(['/', '\\', ':']) >= 0 || image.Contains('\\')) return null;
            var url = new Uri(BaseUri, key + "/" + image);
            if (url.Scheme != "https" || url.Host != BaseUri.Host ||
                !url.AbsolutePath.StartsWith(BaseUri.AbsolutePath + key + "/", StringComparison.Ordinal)) return null;
            return new CosmeticAsset(url, item.Adaptive, item.Top ?? group.Top,
                item.Left ?? group.Left, item.Width ?? group.Width);
        }
        return null;
    }

    // Upstream offsets are percentages or CSS pixels; no CSS expressions are evaluated.
    internal static double ResolveLength(string value, double reference)
    {
        var text = value.Trim();
        var percent = text.EndsWith('%');
        if (percent) text = text[..^1];
        else if (text.EndsWith("px", StringComparison.Ordinal)) text = text[..^2];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result)) throw new InvalidDataException("Unsupported cosmetic dimension");
        return percent ? reference * result / 100 : result;
    }

    internal static async Task<CosmeticCatalog> DownloadAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        return Parse(await http.GetStringAsync(new Uri(BaseUri, "hats.json"), cancellationToken));
    }

    internal static async Task VerifyDownloadAsync()
    {
        var catalog = await DownloadAsync(CancellationToken.None);
        var count = 0;
        foreach (var (mod, group) in catalog.groups)
            foreach (var id in group.Entries.Keys)
                foreach (var part in new[] { CosmeticPart.Hat, CosmeticPart.HatBack })
                {
                    var asset = catalog.Resolve(id, mod, part);
                    if (asset is null) continue;
                    ResolveLength(asset.Top, 80);
                    ResolveLength(asset.Left, 80);
                    ResolveLength(asset.Width, 80);
                    count++;
                }
        if (count == 0) throw new InvalidDataException("Remote cosmetic catalog contains no resolvable images");
        Console.WriteLine($"[PASS] Live cosmetic catalog: {catalog.groups.Count} groups, {count} resolved front/back entries");
    }

    internal static void Verify()
    {
        var catalog = Parse("""
            {"NONE":{"defaultTop":"-12%","defaultLeft":"2px","defaultWidth":"110%","hats":{
                "hat_Ghost":{"image":"upper.png","back_image":"back.png","multi_color":true},
                "hat_ghost":{"image":"lower.png","top":"-9px"},
                "bad":{"image":"../../outside.png"}}},
             "NoS":{"hats":{"hat_Ghost":{"image":"wrong.png"},"custom":{"image":"custom.png"}}}}
            """);
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var upper = catalog.Resolve("hat_Ghost", "NoS", CosmeticPart.Hat)!;
        Require(upper.Url.AbsolutePath.EndsWith("/NONE/upper.png") && upper.Adaptive && upper.Width == "110%",
            "Vanilla priority/default/adaptive lost");
        Require(catalog.Resolve("hat_ghost", "NoS", CosmeticPart.Hat)?.Top == "-9px", "ID case or override lost");
        Require(catalog.Resolve("hat_Ghost", "NoS", CosmeticPart.HatBack)?.Url.AbsolutePath.EndsWith("/back.png") == true,
            "Back image selection failed");
        Require(catalog.Resolve("custom", "NoS", CosmeticPart.Visor)?.Url.AbsolutePath.EndsWith("/NoS/custom.png") == true,
            "MOD fallback failed");
        Require(catalog.Resolve("custom", "NoS", CosmeticPart.HatBack) is null &&
            catalog.Resolve("missing", "NoS", CosmeticPart.Skin) is null &&
            catalog.Resolve("bad", "NoS", CosmeticPart.Hat) is null, "Missing/unsafe image accepted");
        Require(ResolveLength("-12%", 80) == -9.6 && ResolveLength("2px", 80) == 2, "CSS dimension conversion failed");
        Console.WriteLine("[PASS] Cosmetic catalog ID case, vanilla priority, MOD fallback, layers, adaptive colors and dimensions");
    }
}
