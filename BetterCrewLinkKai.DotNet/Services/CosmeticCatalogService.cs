using System.Net.Http;
using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class CosmeticCatalogService
{
    private const string BaseUrl = "https://cdn.jsdelivr.net/gh/OhMyGuus/BetterCrewLink-Hats@master/";
    private static readonly HttpClient HttpClient = new();
    private readonly Dictionary<string, HatEntry> hats = new(StringComparer.OrdinalIgnoreCase);
    private bool initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        try
        {
            await using var stream = await HttpClient.GetStreamAsync($"{BaseUrl}hats.json", cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var modElement in document.RootElement.EnumerateObject())
            {
                var mod = modElement.Name;
                var modRoot = modElement.Value;
                var defaultWidth = ReadDouble(modRoot, "defaultWidth");
                var defaultTop = ReadDouble(modRoot, "defaultTop");
                var defaultLeft = ReadDouble(modRoot, "defaultLeft");
                if (!modRoot.TryGetProperty("hats", out var hatsElement))
                {
                    continue;
                }

                foreach (var hatElement in hatsElement.EnumerateObject())
                {
                    var entry = hatElement.Value;
                    hats[$"{mod}:{hatElement.Name}"] = new HatEntry(
                        mod,
                        entry.TryGetProperty("image", out var image) ? image.GetString() ?? string.Empty : string.Empty,
                        entry.TryGetProperty("back_image", out var backImage) ? backImage.GetString() ?? string.Empty : string.Empty,
                        ReadDouble(entry, "top", defaultTop),
                        ReadDouble(entry, "left", defaultLeft),
                        ReadDouble(entry, "width", defaultWidth));
                }
            }
        }
        catch
        {
            // Cosmetics are optional; the vector crewmate fallback still renders.
        }
        finally
        {
            initialized = true;
        }
    }

    public CosmeticImageSet GetImages(Player player, AmongUsModType modType)
    {
        return new CosmeticImageSet
        {
            HatFront = GetImage(player.HatId, modType, back: false),
            HatBack = GetImage(player.HatId, modType, back: true),
            Skin = GetImage(player.SkinId, modType, back: false),
            Visor = GetImage(player.VisorId, modType, back: false),
            Hat = GetPlacement(player.HatId, modType),
            SkinPlacement = GetPlacement(player.SkinId, modType),
            VisorPlacement = GetPlacement(player.VisorId, modType)
        };
    }

    private string GetImage(string id, AmongUsModType modType, bool back)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var mod = ToCatalogMod(modType);
        var entry = FindEntry("NONE", id) ?? FindEntry(mod, id);
        var image = back ? entry?.BackImage : entry?.Image;
        return string.IsNullOrWhiteSpace(image) || entry is null ? string.Empty : $"{BaseUrl}{entry.Mod}/{image}";
    }

    private HatEntry? FindEntry(string mod, string id)
    {
        return hats.TryGetValue($"{mod}:{id}", out var entry) ? entry : null;
    }

    private CosmeticPlacement GetPlacement(string id, AmongUsModType modType)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return CosmeticPlacement.Empty;
        }

        var entry = FindEntry("NONE", id) ?? FindEntry(ToCatalogMod(modType), id);
        return entry is null
            ? CosmeticPlacement.Empty
            : new CosmeticPlacement
            {
                TopPercent = entry.Top,
                LeftPercent = entry.Left,
                WidthPercent = entry.Width
            };
    }

    private static double ReadDouble(JsonElement element, string propertyName, double fallback = 0)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetDouble(),
            JsonValueKind.String when double.TryParse(
                property.GetString()?.Replace("%", string.Empty),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) => value,
            _ => fallback
        };
    }

    private static string ToCatalogMod(AmongUsModType modType)
    {
        return modType switch
        {
            AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
            AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
            AmongUsModType.TownOfUs => "TOWN_OF_US",
            AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
            AmongUsModType.LasMonjas => "LAS_MONJAS",
            _ => "NONE"
        };
    }

    private sealed record HatEntry(string Mod, string Image, string BackImage, double Top, double Left, double Width);
}
