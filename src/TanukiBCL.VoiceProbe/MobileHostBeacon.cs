using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class MobileHostBeacon
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public static object? Create(AmongUsState? state, bool enabled)
    {
        if (!enabled || state is null ||
            state.GameState is GameState.Menu or GameState.Unknown ||
            string.IsNullOrWhiteSpace(state.LobbyCode)) return null;

        return new
        {
            to = state.LobbyCode + "_mobile",
            data = new { mobileHostInfo = new { isHostingMobile = true, isGameHost = state.IsHost } }
        };
    }

    public static bool IsResponseForLobby(JsonElement data, AmongUsState? state)
    {
        if (state is null || state.GameState == GameState.Menu ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("mobilePlayerInfo", out var playerInfo) ||
            playerInfo.ValueKind != JsonValueKind.Object ||
            !playerInfo.TryGetProperty("code", out var code)) return false;
        return code.ValueKind == JsonValueKind.String &&
               code.GetString() == state.LobbyCode;
    }
}
