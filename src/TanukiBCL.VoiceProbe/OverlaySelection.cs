using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed record OverlayPeerStatus(bool Connected, bool VoiceActive, bool UsingRadio);

internal sealed record OverlayPlayer(Player Player, bool VoiceActive, bool Talking,
    bool UsingRadio);

internal static class OverlaySelection
{
    // TanukiBCL 3.2.7 Overlay.tsx: remote ghosts are hidden from living
    // listeners, disconnected peers are omitted, and compact mode selects VAD
    // activity before the vent-specific speaking ring is suppressed.
    public static IReadOnlyList<OverlayPlayer> Select(AmongUsState state,
        IReadOnlyDictionary<int, OverlayPeerStatus> peers, bool localTalking,
        bool microphoneMuted, bool compact, bool localUsingRadio = false)
    {
        var localAlive = state.Players.FirstOrDefault(player => player.IsLocal)?.IsDead == false;
        var result = new List<OverlayPlayer>();
        foreach (var player in state.Players
            .Where(player => !localAlive || player.IsLocal || !player.IsDead)
            .OrderBy(player => player.Disconnected || player.IsDead)
            .ThenBy(player => player.Id))
        {
            peers.TryGetValue(player.ClientId, out var peer);
            if (!player.IsLocal && peer?.Connected != true) continue;
            var vadHidden = player.ShiftedColor >= 0 && state.GameState != GameState.Discussion;
            var active = (!vadHidden || player.IsLocal) &&
                (player.IsLocal ? localTalking && !microphoneMuted : peer?.VoiceActive == true);
            if (compact && !active) continue;
            result.Add(new OverlayPlayer(player, active, active && !player.InVent,
                player.IsLocal ? localUsingRadio : peer?.UsingRadio == true));
        }
        return result;
    }
}
