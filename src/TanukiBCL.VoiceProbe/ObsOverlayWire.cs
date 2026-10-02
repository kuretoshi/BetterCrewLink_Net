using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed record ObsPeerState(bool Connected, bool Talking, bool UsingRadio);

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
    {
        var local = state.Players.FirstOrDefault(player => player.IsLocal);
        var playerStates = state.Players.Select(player =>
        {
            peers.TryGetValue(player.ClientId, out var peer);
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
                ["usingRadio"] = player.IsLocal ? localUsingRadio : peer?.UsingRadio == true,
                ["connected"] = peer?.Connected == true
            };
            if (state.Mod == AmongUsModType.NebulaOnTheShip)
            {
                if (NosColorHex(player.NosPlayer) is { } nosColor) data["nosColor"] = nosColor;
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
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string ColorHex(uint packed) =>
        $"#{(byte)packed:x2}{(byte)(packed >> 8):x2}{(byte)(packed >> 16):x2}";

    private static string? NosColorHex(NosPlayerData? data)
    {
        if (data is null || !double.IsFinite(data.ColorR) ||
            !double.IsFinite(data.ColorG) || !double.IsFinite(data.ColorB)) return null;
        static int Byte(double value) => (int)Math.Round(Math.Clamp(value, 0d, 1d) * 255d,
            MidpointRounding.AwayFromZero);
        return $"#{Byte(data.ColorR):x2}{Byte(data.ColorG):x2}{Byte(data.ColorB):x2}";
    }

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
