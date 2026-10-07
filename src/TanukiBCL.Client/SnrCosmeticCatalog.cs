using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TanukiBCL.Client;

// Released 3.2.7 renderer/lib/cosmetics.ts remote SNR definitions.
internal sealed class SnrCosmeticCatalog
{
    private static readonly Uri BaseUri = new("https://raw.githubusercontent.com/SuperNewRoles/SuperNewCosmetics/main/");
    private sealed record Definition(string Name, string Package, string Resource, string Back, bool Adaptive, bool SnrLayout);
    private readonly Definition[] hats;
    private readonly Definition[] visors;
    private SnrCosmeticCatalog(Definition[] hats, Definition[] visors) { this.hats = hats; this.visors = visors; }

    internal static SnrCosmeticCatalog Parse(string hats, string visors) =>
        new(ParseDefinitions(hats, "hats"), ParseDefinitions(visors, "Visors"));

    private static Definition[] ParseDefinitions(string json, string property)
    {
        using var doc = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        if (!doc.RootElement.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) return [];
        static string Text(JsonElement item, string key, string fallback = "") =>
            item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
        static bool True(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        return items.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new Definition(
            Text(x, "name"), Text(x, "package", "NONE_PACKAGE"), Text(x, "resource"), Text(x, "backresource"),
            True(x, "adaptive"), True(x, "IsSNR") || Text(x, "IsSNR") == "true" || True(x, "isSNR"))).ToArray();
    }

    private static string Plain(string value) => Regex.Replace(value, "<[^>]*>", "");
    private static string Normalize(string value)
    {
        value = Regex.Replace(Plain(value), @"\.png$", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"_(front|idle|back|flip|adaptive|bounce)(_(front|idle|back|flip|adaptive|bounce))*$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(value, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    }

    internal CosmeticAsset? Resolve(string id, CosmeticPart part)
    {
        var definition = FindDefinition(id, part);
        if (definition is null) return null;
        var resource = part == CosmeticPart.HatBack ? definition.Back : definition.Resource;
        if (string.IsNullOrEmpty(resource)) return null;
        var url = new Uri(BaseUri, (part == CosmeticPart.Visor ? "Visors/" : "hats/") + Uri.EscapeDataString(resource));
        return new CosmeticAsset(url, definition.Adaptive || Regex.IsMatch(resource, @"_adaptive(?:_|\.)", RegexOptions.IgnoreCase),
            "-52%", "-18px", "140%", part == CosmeticPart.Visor && definition.SnrLayout);
    }

    internal CosmeticAsset ApplyLocalMetadata(string id, CosmeticPart part, CosmeticAsset local)
    {
        // v3.2.13 retains optional definitions for layout/tint, never for the PNG URL.
        if (part == CosmeticPart.Skin) return local;
        var definition = FindDefinition(id, part);
        if (definition is null) return local;
        return local with
        {
            Adaptive = local.Adaptive || definition.Adaptive ||
                Regex.IsMatch(definition.Resource, @"_adaptive(?:_|\.)", RegexOptions.IgnoreCase),
            SnrVisorLayout = part == CosmeticPart.Visor && definition.SnrLayout
        };
    }

    private Definition? FindDefinition(string id, CosmeticPart part)
    {
        if (!id.StartsWith("Modded_", StringComparison.Ordinal)) return null;
        var definitions = part == CosmeticPart.Visor ? visors : hats;
        var plain = Plain(id);
        var definition = definitions.FirstOrDefault(x => plain == $"Modded_{x.Package}_{x.Name}");
        var normalized = Normalize(plain);
        return definition ?? definitions.FirstOrDefault(x => Normalize(x.Resource) is { Length: > 0 } resource &&
            normalized.EndsWith(resource, StringComparison.Ordinal));
    }

    internal static CosmeticAsset WithImageSize(CosmeticAsset asset, int width, int height)
    {
        if (!asset.SnrVisorLayout || width <= 0 || height <= 0) return asset;
        var w = Math.Min(width / (115 * (8d / 3)) * 140, 140);
        var h = height / (double)width * w;
        static string Format(double value, string unit) => value.ToString("R", CultureInfo.InvariantCulture) + unit;
        return asset with { Width = Format(w, "%"), Left = Format(56 - w / 2, "px"), Top = Format(22 - h / 2, "%") };
    }

    internal static async Task<SnrCosmeticCatalog> DownloadAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        var hats = http.GetStringAsync(new Uri(BaseUri, "CustomHats.json"));
        var visors = http.GetStringAsync(new Uri(BaseUri, "CustomVisors.json"));
        await Task.WhenAll(hats, visors).ConfigureAwait(false);
        return Parse(await hats, await visors);
    }

    internal static void Verify()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var catalog = Parse("""
            {"hats":[{"name":"Name","package":"Pack","resource":"color_adaptive_front.png","backresource":"back.png"},
            {"name":"Exact","package":"P","resource":"different.png","adaptive":true},
            {"name":"Default","resource":"日本 語.png"}]}
            """, """
            {"Visors":[{"name":"V","package":"P","resource":"v.png","IsSNR":"true"},
            {"name":"Lower","package":"P","resource":"lower.png","isSNR":"true"}]}
            """);
        var front = catalog.Resolve("Modded_Pack_<b>Name</b>", CosmeticPart.Hat)!;
        Require(front.Adaptive && front.Width == "140%" && front.Url.AbsolutePath.EndsWith("/hats/color_adaptive_front.png"), "SNR exact/adaptive/default layout failed");
        Require(catalog.Resolve("Modded_any_color", CosmeticPart.Hat)?.Url == front.Url, "SNR resource suffix normalization failed");
        Require(catalog.Resolve("Modded_Pack_Name", CosmeticPart.HatBack)?.Adaptive == false, "Front filename adaptive leaked to back");
        Require(catalog.Resolve("Modded_P_Exact", CosmeticPart.Hat)?.Adaptive == true, "Explicit adaptive flag ignored");
        Require(catalog.Resolve("Modded_NONE_PACKAGE_Default", CosmeticPart.Hat)?.Url.Host == BaseUri.Host, "Missing package or escaped resource failed");
        Require(catalog.Resolve("Name", CosmeticPart.Hat) is null && catalog.Resolve("Modded_missing", CosmeticPart.Hat) is null &&
            catalog.Resolve("Modded_P_Exact", CosmeticPart.HatBack) is null, "Unresolved SNR part must fall back");
        var visor = catalog.Resolve("Modded_P_V", CosmeticPart.Visor)!;
        var layout = WithImageSize(visor, 920, 460);
        Require(layout.Width == "140%" && layout.Left == "-14px" && layout.Top == "-13%", "Large visor canvas clamp/aspect failed");
        layout = WithImageSize(visor, 115, 230);
        Require(Math.Abs(CosmeticCatalog.ResolveLength(layout.Width, 100) - 52.5) < 0.000001 &&
            Math.Abs(CosmeticCatalog.ResolveLength(layout.Left, 100) - 29.75) < 0.000001 &&
            Math.Abs(CosmeticCatalog.ResolveLength(layout.Top, 100) + 30.5) < 0.000001, "Small visor natural size failed");
        Require(!catalog.Resolve("Modded_P_Lower", CosmeticPart.Visor)!.SnrVisorLayout, "Lowercase string flag must not count as boolean");
        var local = new CosmeticAsset(new Uri("file:///C:/game/local.png"), false, "-52%", "-18px", "140%");
        Require(catalog.ApplyLocalMetadata("Modded_P_Exact", CosmeticPart.Hat, local) is { Adaptive: true } adapted &&
            adapted.Url == local.Url &&
            catalog.ApplyLocalMetadata("Modded_P_V", CosmeticPart.Visor, local).SnrVisorLayout &&
            !catalog.ApplyLocalMetadata("Modded_P_Exact", CosmeticPart.Skin, local).Adaptive,
            "SNR local PNG metadata replaced the URL or tinted a skin");
        Console.WriteLine("[PASS] SNR remote IDs, suffixes, front/back, adaptive flags and natural-size visor layout");
    }

    internal static async Task VerifyDownloadAsync()
    {
        var catalog = await DownloadAsync();
        foreach (var (entries, part) in new[] { (catalog.hats, CosmeticPart.Hat), (catalog.visors, CosmeticPart.Visor) })
        {
            var entry = entries.First(x => !string.IsNullOrEmpty(x.Resource));
            var asset = catalog.Resolve($"Modded_{entry.Package}_{entry.Name}", part)
                ?? throw new InvalidOperationException("Live SNR definition did not resolve");
            var bitmap = await CosmeticImages.GetImageAsync(asset.Url);
            if (!bitmap.IsFrozen || bitmap.PixelWidth <= 0) throw new InvalidOperationException("Live SNR image decode failed");
            Console.WriteLine($"[PASS] Live SNR {part}: {entries.Length} definitions; sample image {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        }
    }
}
