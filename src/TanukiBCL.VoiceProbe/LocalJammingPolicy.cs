using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class LocalJammingPolicy
{
    // Matches released 3.2.8 VoiceController.onGameState -> AudioController.setJammed.
    public static bool IsJammed(AmongUsState? state, LobbySettings settings) =>
        state?.Mod == AmongUsModType.NebulaOnTheShip &&
        settings.NosFixerJammingVoiceBlock &&
        state.Players.Any(player => player.IsLocal && player.NosPlayer?.IsJammed == true);
}
