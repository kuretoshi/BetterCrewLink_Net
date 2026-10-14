using System.Globalization;
using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal readonly record struct ObsPeerState(bool Connected, bool Talking, bool UsingRadio);

internal static class ObsOverlayWire
{
    private static readonly string[][] DefaultPlayerColors =
    [
        ["#C51111", "#7A0838"], ["#132ED1", "#09158E"], ["#117F2D", "#0A4D2E"],
        ["#ED54BA", "#AB2BAD"], ["#EF7D0D", "#B33E15"], ["#F5F557", "#C38823"],
        ["#3F474E", "#1E1F26"], ["#FFFFFF", "#8394BF"], ["#6B2FBB", "#3B177C"],
        ["#71491E", "#5E2615"], ["#38FEDC", "#24A8BE"], ["#50EF39", "#15A742"]
    ];

    public static JsonElement Build(AmongUsState state,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool localTalking, bool localUsingRadio)
        => JsonSerializer.SerializeToElement(CreatePayload(state, peers, localTalking, localUsingRadio));

    public static void Write(Utf8JsonWriter writer, AmongUsState state,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool localTalking, bool localUsingRadio)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("overlayState");
        writer.WriteNumber("gameState", (int)state.GameState);
        writer.WriteStartArray("players");
        foreach (var player in state.Players)
            WritePlayer(writer, state, player, peers, localUsingRadio);
        writer.WriteEndArray();
        writer.WriteEndObject();
        WriteClientMap(writer, "otherTalking", state.Players, peers, talking: true);
        WriteClientMap(writer, "otherDead", state.Players, peers, talking: false);
        writer.WriteBoolean("localTalking", localTalking);
        writer.WriteBoolean("localIsAlive", state.Players.FirstOrDefault(player => player.IsLocal)?.IsDead != true);
        writer.WriteString("mod", ModName(state.Mod));
        writer.WriteBoolean("oldMeetingHud", state.OldMeetingHud);
        writer.WriteEndObject();
    }

    private static void WritePlayer(Utf8JsonWriter writer, AmongUsState state, Player player,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool localUsingRadio)
    {
        var hasPeer = peers.TryGetValue(player.ClientId, out var peer);
        writer.WriteStartObject();
        writer.WriteNumber("id", player.Id);
        writer.WriteNumber("clientId", player.ClientId);
        writer.WriteBoolean("inVent", player.InVent);
        writer.WriteBoolean("isDead", player.IsDead);
        writer.WriteString("name", player.Name);
        writer.WriteString("hatId", player.HatId);
        writer.WriteNumber("petId", player.PetId);
        writer.WriteString("skinId", player.SkinId);
        writer.WriteString("visorId", player.VisorId);
        writer.WriteBoolean("disconnected", player.Disconnected);
        writer.WriteBoolean("isLocal", player.IsLocal);
        writer.WriteBoolean("bugged", player.Bugged);
        writer.WriteBoolean("usingRadio", player.IsLocal ? localUsingRadio : hasPeer && peer.UsingRadio);
        writer.WriteBoolean("connected", hasPeer && peer.Connected);
        if (state.Mod == AmongUsModType.NebulaOnTheShip)
        {
            if (NosColor.For(player) is { } nosColor) writer.WriteString("nosColor", nosColor);
        }
        else WritePlayerColor(writer, state, player);
        writer.WriteEndObject();
    }

    private static void WritePlayerColor(Utf8JsonWriter writer, AmongUsState state, Player player)
    {
        writer.WriteNumber("colorId", player.ColorId);
        writer.WriteNumber("shiftedColor", player.ShiftedColor);
        if (player.ColorId >= 0 && player.ColorId < state.PlayerColors.Count)
        {
            var colors = state.PlayerColors[player.ColorId];
            writer.WriteStartArray("realColor");
            writer.WriteStringValue(ColorHex(colors.Main));
            writer.WriteStringValue(ColorHex(colors.Shadow));
            writer.WriteEndArray();
        }
        else if (player.ColorId >= 0 && player.ColorId < DefaultPlayerColors.Length)
        {
            writer.WriteStartArray("realColor");
            foreach (var color in DefaultPlayerColors[player.ColorId]) writer.WriteStringValue(color);
            writer.WriteEndArray();
        }
    }

    private static void WriteClientMap(Utf8JsonWriter writer, string name, List<Player> players,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool talking)
    {
        writer.WriteStartObject(name);
        Span<char> key = stackalloc char[11];
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player.IsLocal || SeenEarlier(players, i)) continue;
            player.ClientId.TryFormat(key, out var length, provider: CultureInfo.InvariantCulture);
            var value = talking ? peers.TryGetValue(player.ClientId, out var peer) && peer.Talking
                : LastDead(players, i);
            writer.WriteBoolean(key[..length], value);
        }
        writer.WriteEndObject();
    }

    private static bool SeenEarlier(List<Player> players, int index)
    {
        for (var i = 0; i < index; i++)
            if (!players[i].IsLocal && players[i].ClientId == players[index].ClientId) return true;
        return false;
    }

    private static bool LastDead(List<Player> players, int index)
    {
        var clientId = players[index].ClientId;
        var dead = players[index].IsDead;
        for (var i = index + 1; i < players.Count; i++)
            if (!players[i].IsLocal && players[i].ClientId == clientId) dead = players[i].IsDead;
        return dead;
    }

    private static object CreatePayload(AmongUsState state,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool localTalking, bool localUsingRadio)
    {
        var local = state.Players.FirstOrDefault(player => player.IsLocal);
        var playerStates = state.Players.Select(player =>
        {
            var hasPeer = peers.TryGetValue(player.ClientId, out var peer);
            var data = new Dictionary<string, object?>
            {
                ["id"] = player.Id,
                ["clientId"] = player.ClientId,
                ["inVent"] = player.InVent,
                ["isDead"] = player.IsDead,
                ["name"] = player.Name,
                ["hatId"] = player.HatId,
                ["petId"] = player.PetId,
                ["skinId"] = player.SkinId,
                ["visorId"] = player.VisorId,
                ["disconnected"] = player.Disconnected,
                ["isLocal"] = player.IsLocal,
                ["bugged"] = player.Bugged,
                ["usingRadio"] = player.IsLocal ? localUsingRadio : hasPeer && peer.UsingRadio,
                ["connected"] = hasPeer && peer.Connected
            };
            if (state.Mod == AmongUsModType.NebulaOnTheShip)
            {
                if (NosColor.For(player) is { } nosColor) data["nosColor"] = nosColor;
            }
            else
            {
                data["colorId"] = player.ColorId;
                data["shiftedColor"] = player.ShiftedColor;
                if (player.ColorId >= 0 && player.ColorId < state.PlayerColors.Count)
                {
                    var colors = state.PlayerColors[player.ColorId];
                    data["realColor"] = new[] { ColorHex(colors.Main), ColorHex(colors.Shadow) };
                }
                else if (player.ColorId >= 0 && player.ColorId < DefaultPlayerColors.Length)
                {
                    data["realColor"] = DefaultPlayerColors[player.ColorId];
                }
            }
            return data;
        }).ToArray();
        var payload = new
        {
            overlayState = new { gameState = (int)state.GameState, players = playerStates },
            otherTalking = state.Players.Where(player => !player.IsLocal)
                .GroupBy(player => player.ClientId)
                .ToDictionary(group => group.Key,
                    group => peers.TryGetValue(group.Key, out var peer) && peer.Talking),
            otherDead = state.Players.Where(player => !player.IsLocal)
                .GroupBy(player => player.ClientId)
                .ToDictionary(group => group.Key, group => group.Last().IsDead),
            localTalking,
            localIsAlive = local?.IsDead != true,
            mod = ModName(state.Mod),
            oldMeetingHud = state.OldMeetingHud
        };
        return payload;
    }

    private static string ColorHex(uint packed) =>
        $"#{(byte)packed:x2}{(byte)(packed >> 8):x2}{(byte)(packed >> 16):x2}";

    private static string ModName(AmongUsModType mod) => mod switch
    {
        AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
        AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
        AmongUsModType.TownOfUs => "TOWN_OF_US",
        AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
        AmongUsModType.LasMonjas => "LAS_MONJAS",
        AmongUsModType.NebulaOnTheShip => "NoS",
        AmongUsModType.TownOfHostForE => "TOH4E",
        AmongUsModType.Other => "OTHER",
        _ => "NONE"
    };
}
