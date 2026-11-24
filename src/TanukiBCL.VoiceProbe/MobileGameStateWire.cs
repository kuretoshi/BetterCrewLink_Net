using System.Text.Json;
using System.Text.Json.Nodes;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed record MobileCosmeticFrame(
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> PlayerParts,
    IReadOnlyDictionary<string, string>? Assets = null,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>>? SnrPlayerParts = null,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, bool>>? SnrPlayerScale = null);

internal static class MobileGameStateWire
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static object Create(AmongUsState state, LobbySettings settings, MobileCosmeticFrame? cosmetics)
    {
        // The browser expects the Electron game's camel-case state and string MOD IDs.
        // Serialize a copy: changing the live Player objects would affect voice mixing and WPF.
        var game = JsonSerializer.SerializeToNode(state, Options)!.AsObject();
        game["mod"] = state.Mod switch
        {
            AmongUsModType.NebulaOnTheShip => "NoS",
            AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
            AmongUsModType.TownOfHostForE => "TOH4E",
            AmongUsModType.TownOfUs => "TOWN_OF_US",
            AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
            AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
            AmongUsModType.LasMonjas => "LAS_MONJAS",
            AmongUsModType.Other => "OTHER",
            _ => "NONE"
        };
        game["comsSabotaged"] = state.CommsSabotaged;
        if (game["players"] is JsonArray players && cosmetics is not null)
            for (var index = 0; index < players.Count && index < state.Players.Count; index++)
                if (players[index] is JsonObject player)
                {
                    if (cosmetics.PlayerParts.TryGetValue(state.Players[index].Id, out var parts))
                        player["nosCosmetics"] = JsonSerializer.SerializeToNode(parts, Options);
                    if (cosmetics.SnrPlayerParts?.TryGetValue(state.Players[index].Id, out var snrParts) == true)
                        player["snrCosmetics"] = JsonSerializer.SerializeToNode(snrParts, Options);
                    if (cosmetics.SnrPlayerScale?.TryGetValue(state.Players[index].Id, out var scale) == true)
                        player["snrCosmeticScale"] = JsonSerializer.SerializeToNode(scale, Options);
                }

        return new
        {
            gameState = game,
            nosVoiceFrameTime = state.Mod == AmongUsModType.NebulaOnTheShip
                ? (long?)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
            activeLobbySettings = JsonSerializer.SerializeToNode(settings.Normalize(), Options),
            nosCosmeticAssets = cosmetics?.Assets
        };
    }

    internal static IReadOnlyList<string>? RequestedIds(JsonElement data, AmongUsState? state)
    {
        if (!MobileHostBeacon.IsResponseForLobby(data, state)) return null;
        var player = data.GetProperty("mobilePlayerInfo");
        if (!player.TryGetProperty("nosCosmeticIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return null;
        return ids.EnumerateArray().Take(75)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(id => id.Length == 64 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            .ToArray();
    }
}
