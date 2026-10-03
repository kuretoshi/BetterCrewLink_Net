using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class PublicLobbyAnnouncement
{
    internal static Dictionary<string, object> Build(AmongUsState state, LobbySettings settings)
    {
        var host = state.Players.FirstOrDefault(player => player.IsLocal)?.Name ?? string.Empty;
        return new Dictionary<string, object>
        {
            ["id"] = -1,
            ["title"] = settings.PublicLobbyTitle,
            ["host"] = host,
            ["current_players"] = state.Players.Count,
            ["max_players"] = state.MaxPlayers,
            ["language"] = settings.PublicLobbyLanguage,
            ["mods"] = ModId(state.Mod),
            ["isPublic"] = settings.PublicLobbyOn,
            ["gameState"] = (int)state.GameState
        };
    }

    private static string ModId(AmongUsModType mod) => mod switch
    {
        AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
        AmongUsModType.TownOfHostForE => "TOH4E",
        AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
        AmongUsModType.TownOfUs => "TOWN_OF_US",
        AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
        AmongUsModType.LasMonjas => "LAS_MONJAS",
        AmongUsModType.NebulaOnTheShip => "NoS",
        AmongUsModType.Other => "OTHER",
        _ => "NONE"
    };

    internal static void Verify()
    {
        var state = new AmongUsState
        {
            LobbyCode = "ABCDEF", GameState = GameState.Lobby, Mod = AmongUsModType.NebulaOnTheShip,
            MaxPlayers = 15,
            Players =
            [
                new Player { Name = "host", IsLocal = true },
                new Player { Name = "guest" }
            ]
        };
        var settings = new LobbySettings
        {
            PublicLobbyOn = true, PublicLobbyTitle = "test", PublicLobbyLanguage = "ja"
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(Build(state, settings)));
        var payload = document.RootElement;
        if (payload.GetProperty("id").GetInt32() != -1 ||
            payload.GetProperty("title").GetString() != "test" ||
            payload.GetProperty("host").GetString() != "host" ||
            payload.GetProperty("current_players").GetInt32() != 2 ||
            payload.GetProperty("max_players").GetInt32() != 15 ||
            payload.GetProperty("language").GetString() != "ja" ||
            payload.GetProperty("mods").GetString() != "NoS" ||
            !payload.GetProperty("isPublic").GetBoolean() ||
            payload.GetProperty("gameState").GetInt32() != 0)
            throw new InvalidOperationException("Released 3.2.8 public lobby payload does not match the wire schema");
    }
}
